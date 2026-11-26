#include "wasapi.h"
#include <avrt.h>
#include <array>
#include <chrono>
#include <thread>

namespace {
using namespace se;
constexpr uint32_t max_outputs = 16;
constexpr uint32_t fifo_capacity = 1u << 19;
constexpr HRESULT timeout = HRESULT_FROM_WIN32(WAIT_TIMEOUT);

struct MultimediaThread {
    DWORD task = 0;
    HANDLE handle = AvSetMmThreadCharacteristicsW(L"Audio", &task);
    ~MultimediaThread() { if (handle) AvRevertMmThreadCharacteristics(handle); }
};

struct Counters {
    std::atomic<uint32_t> state{SE_CONNECTING}, discontinuities{0};
    std::atomic<int32_t> error{0};
    std::atomic<uint64_t> underruns{0}, overruns{0}, reconnects{0}, position{0}, timestamp{0};
    std::atomic<float> queue{0}, latency{0}, drift{0};

    void set(uint32_t value, HRESULT reason = S_OK) noexcept {
        error.store(reason, std::memory_order_relaxed);
        if (value != SE_STREAMING) {
            queue.store(0, std::memory_order_relaxed);
            latency.store(0, std::memory_order_relaxed);
            drift.store(0, std::memory_order_relaxed);
        }
        state.store(value, std::memory_order_release);
    }

    se_status read() const noexcept {
        return {state.load(std::memory_order_acquire), error.load(), underruns.load(),
            overruns.load(), reconnects.load(), position.load(), timestamp.load(),
            queue.load(), latency.load(), drift.load(), discontinuities.load()};
    }
};

struct Output {
    explicit Output(std::wstring endpoint) : id(std::move(endpoint)) {}
    const std::wstring id;
    Ring input{fifo_capacity};
    ResamplingKernel kernel;
    Counters stats;
    std::atomic<bool> enabled{true};
    std::atomic<float> gain{1};
    std::atomic<uint32_t> delay{0};
    std::atomic<uint64_t> test_before{0};
    std::thread thread;
};

bool valid_id(const wchar_t* id) noexcept {
    return id && id[0] && wcsnlen_s(id, 2049) <= 2048;
}

template<class Action>
HRESULT guarded(Action&& action) noexcept {
    try { return action(); }
    catch (const std::bad_alloc&) { return E_OUTOFMEMORY; }
    catch (...) { return E_FAIL; }
}
}

struct se_session {
    explicit se_session(const wchar_t* id) : source(id), stop(CreateEventW(nullptr, TRUE, FALSE, nullptr)) {}

    ~se_session() {
        if (stop) SetEvent(stop.get());
        if (capture.joinable()) capture.join();
        for (auto& output : outputs)
            if (output && output->thread.joinable()) output->thread.join();
    }

    Output* find(const wchar_t* id) const noexcept {
        for (uint32_t index = 0; index < count.load(std::memory_order_acquire); ++index)
            if (outputs[index]->id == id) return outputs[index].get();
        return nullptr;
    }

    void reset_capture(uint32_t next_rate) noexcept {
        const auto next_epoch = (capture_format.load(std::memory_order_relaxed) >> 32) + 1;
        capture_format.store((next_epoch << 32) | next_rate, std::memory_order_release);
        if (!next_rate) last_signal.store(0, std::memory_order_relaxed);
    }

    HRESULT run_capture(bool& previously_streamed) {
        Stream stream;
        if (const auto hr = stream.open(source, true); FAILED(hr)) return hr;
        ComPtr<IAudioCaptureClient> input;
        if (const auto hr = stream.client->GetService(IID_PPV_ARGS(&input)); FAILED(hr)) return hr;
        std::array<Frame, 512> decoded;
        if (const auto hr = stream.client->Start(); FAILED(hr)) return hr;
        stream.started = true;
        if (previously_streamed) ++source_stats.reconnects;
        previously_streamed = true;
        reset_capture(stream.format.rate);
        source_stats.set(SE_STREAMING);
        MultimediaThread priority;
        while (true) {
            auto hr = stream.wait(stop.get());
            if (hr == timeout) {
                UINT32 padding = 0;
                // A quiet loopback endpoint can legitimately produce no callbacks.
                hr = stream.client->GetCurrentPadding(&padding);
            }
            if (hr != S_OK) return hr;
            while (WaitForSingleObject(stop.get(), 0) == WAIT_TIMEOUT) {
                UINT32 pending = 0;
                if (const auto result = input->GetNextPacketSize(&pending); FAILED(result)) return result;
                if (!pending) break;
                BYTE* data = nullptr;
                UINT32 frames = 0;
                DWORD flags = 0;
                UINT64 position = 0, timestamp = 0;
                if (const auto result = input->GetBuffer(&data, &frames, &flags, &position, &timestamp);
                    FAILED(result)) return result;
                if (flags & AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY) {
                    ++source_stats.discontinuities;
                    reset_capture(stream.format.rate);
                }
                if (!(flags & AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR)) {
                    source_stats.position.store(position);
                    source_stats.timestamp.store(timestamp);
                }
                bool signal = false;
                for (uint32_t offset = 0; offset < frames;) {
                    const auto chunk = std::min(frames - offset, static_cast<uint32_t>(decoded.size()));
                    for (uint32_t frame = 0; frame < chunk; ++frame) {
                        decoded[frame] = (flags & AUDCLNT_BUFFERFLAGS_SILENT) ? Frame{} :
                            stream.format.read(data + static_cast<size_t>(offset + frame) * stream.format.stride);
                        signal |= std::abs(decoded[frame].left) >= 0.001f || std::abs(decoded[frame].right) >= 0.001f;
                    }
                    const auto active = count.load(std::memory_order_acquire);
                    for (uint32_t index = 0; index < active; ++index) {
                        auto& destination = *outputs[index];
                        if (destination.enabled.load(std::memory_order_relaxed)) {
                            const auto written = destination.input.push(decoded.data(), chunk);
                            if (written != chunk) ++destination.stats.overruns;
                        }
                    }
                    offset += chunk;
                }
                if (signal) last_signal.store(GetTickCount64(), std::memory_order_relaxed);
                if (const auto result = input->ReleaseBuffer(frames); FAILED(result)) return result;
            }
        }
    }

    void capture_loop() noexcept {
        ComApartment apartment;
        if (FAILED(apartment.result)) {
            source_stats.set(SE_RETRYING, apartment.result);
            return;
        }
        bool previously_streamed = false;
        do {
            const auto hr = guarded([&] { return run_capture(previously_streamed); });
            reset_capture(0);
            if (hr == S_FALSE) break;
            source_stats.set(SE_RETRYING, hr);
        } while (WaitForSingleObject(stop.get(), 1000) == WAIT_TIMEOUT);
    }

    HRESULT run_output(Output& output, bool& previously_streamed) {
        Stream stream;
        if (const auto hr = stream.open(output.id, false); FAILED(hr)) return hr;
        ComPtr<IAudioRenderClient> sink;
        if (const auto hr = stream.client->GetService(IID_PPV_ARGS(&sink)); FAILED(hr)) return hr;
        ComPtr<IAudioClock> clock;
        stream.client->GetService(IID_PPV_ARGS(&clock));
        BYTE* buffer = nullptr;
        if (const auto hr = sink->GetBuffer(stream.buffer_frames, &buffer); FAILED(hr)) return hr;
        if (const auto hr = sink->ReleaseBuffer(stream.buffer_frames, AUDCLNT_BUFFERFLAGS_SILENT); FAILED(hr)) return hr;

        if (const auto hr = stream.client->Start(); FAILED(hr)) return hr;
        stream.started = true;
        if (previously_streamed) ++output.stats.reconnects;
        previously_streamed = true;
        output.stats.set(SE_STREAMING);

        auto last_format = capture_format.load(std::memory_order_acquire);
        auto last_overruns = output.stats.overruns.load();
        auto last_delay = output.delay.load();
        const auto render_rate = stream.format.rate;
        TestTone tone(render_rate);
        const auto period_ms = milliseconds(stream.period_frames, render_rate);
        const uint32_t base_ms = std::clamp(static_cast<uint32_t>(period_ms * 3), 60u, 200u);
        Resampler resampler;
        uint32_t kernel_rate = 0;
        bool primed = false;
        output.input.clear();
        MultimediaThread priority;
        while (output.enabled.load()) {
            if (const auto hr = stream.wait(stop.get()); hr != S_OK) return hr;
            UINT32 padding = 0;
            if (const auto hr = stream.client->GetCurrentPadding(&padding); FAILED(hr)) return hr;
            if (padding > stream.buffer_frames) return E_UNEXPECTED;
            const auto frames = stream.buffer_frames - padding;
            if (!frames) continue;

            const auto format = capture_format.load(std::memory_order_acquire);
            const auto overruns = output.stats.overruns.load();
            const auto delay = output.delay.load();
            const auto input_rate = static_cast<uint32_t>(format);
            const auto nominal_step = static_cast<double>(input_rate) / render_rate;
            if (input_rate && kernel_rate != input_rate) {
                output.kernel.configure(nominal_step);
                kernel_rate = input_rate;
            }
            const auto target = input_rate ? target_frames(input_rate, delay, base_ms) : 0;
            const auto queue = output.input.available();
            if (format != last_format || overruns != last_overruns) {
                output.input.clear();
                resampler.reset();
                primed = false;
            } else if (delay > last_delay) {
                // Pause until the queue reaches the new delay in one bounded adjustment.
                resampler.reset();
                primed = false;
            } else if (input_rate && (delay < last_delay || queue > target + input_rate / 5)) {
                if (queue > target) output.input.consume(queue - target);
                resampler.reset();
            }
            last_format = format;
            last_overruns = overruns;
            last_delay = delay;
            const auto ready = output.input.available();
            if (input_rate && ready >= target) primed = true;

            if (const auto hr = sink->GetBuffer(frames, &buffer); FAILED(hr)) return hr;
            const float gain = paused.load(std::memory_order_relaxed) ? 0.0f : output.gain.load(std::memory_order_relaxed);
            const auto test_before = output.test_before.exchange(0, std::memory_order_relaxed);
            if (test_before && test_before >= GetTickCount64() && gain > 0) tone.start();
            const bool testing = tone.active() && gain > 0;
            auto write = [&](uint32_t index, Frame frame) noexcept {
                if (tone.active()) {
                    const auto sample = tone.next();
                    frame = {sample, sample};
                }
                stream.format.write(buffer + static_cast<size_t>(index) * stream.format.stride,
                    {frame.left * gain, frame.right * gain});
            };
            uint32_t rendered = 0;
            if (input_rate && primed && output.enabled.load(std::memory_order_relaxed)) {
                resampler.adjust(milliseconds(ready, input_rate) - milliseconds(target, input_rate),
                    static_cast<float>(frames) / static_cast<float>(render_rate));
                rendered = resampler.render(output.input, frames, nominal_step, output.kernel, write);
                if (rendered != frames) {
                    ++output.stats.underruns;
                    primed = false;
                    resampler.reset();
                }
            }
            if (rendered || tone.active())
                for (uint32_t frame = rendered; frame < frames; ++frame)
                    write(frame, {});
            if (const auto hr = sink->ReleaseBuffer(frames, gain > 0 && (rendered || testing) ? 0 : AUDCLNT_BUFFERFLAGS_SILENT);
                FAILED(hr)) return hr;

            const float queue_ms = input_rate ? milliseconds(output.input.available(), input_rate) : 0;
            output.stats.queue.store(queue_ms);
            output.stats.latency.store(input_rate ? queue_ms + milliseconds(padding + frames, render_rate) : 0);
            output.stats.drift.store(resampler.drift_ppm());
            if (clock) {
                UINT64 position = 0, timestamp = 0;
                if (clock->GetPosition(&position, &timestamp) == S_OK) {
                    output.stats.position.store(position);
                    output.stats.timestamp.store(timestamp);
                }
            }
        }
        return S_OK;
    }

    void output_loop(Output& output) noexcept {
        ComApartment apartment;
        if (FAILED(apartment.result)) {
            output.stats.set(SE_RETRYING, apartment.result);
            return;
        }
        bool previously_streamed = false;
        while (WaitForSingleObject(stop.get(), 0) == WAIT_TIMEOUT) {
            if (!output.enabled.load()) {
                output.stats.set(SE_DISABLED);
                if (WaitForSingleObject(stop.get(), 200) != WAIT_TIMEOUT) return;
                previously_streamed = false;
                continue;
            }
            output.stats.set(SE_CONNECTING);
            const auto hr = guarded([&] { return run_output(output, previously_streamed); });
            if (hr == S_FALSE) return;
            if (FAILED(hr)) {
                output.stats.set(SE_RETRYING, hr);
                if (WaitForSingleObject(stop.get(), 1000) != WAIT_TIMEOUT) return;
            }
        }
    }

    const std::wstring source;
    Event stop;
    std::atomic<uint32_t> count{0};
    std::atomic<uint64_t> capture_format{0};
    std::atomic<uint64_t> last_signal{0};
    std::atomic<bool> paused{false};
    Counters source_stats;
    std::array<std::unique_ptr<Output>, max_outputs> outputs;
    std::thread capture;
};

uint32_t __cdecl se_abi_version() { return 2; }

int32_t __cdecl se_enumerate(se_endpoint_callback callback, void* context) {
    return guarded([&] { return enumerate(callback, context); });
}

int32_t __cdecl se_start(const wchar_t* source, se_session** session) {
    if (!session) return E_POINTER;
    *session = nullptr;
    if (!valid_id(source)) return E_INVALIDARG;
    return guarded([&] {
        auto pending = std::make_unique<se_session>(source);
        if (!pending->stop) return HRESULT_FROM_WIN32(GetLastError());
        auto* instance = pending.get();
        pending->capture = std::thread([instance] { instance->capture_loop(); });
        *session = pending.release();
        return S_OK;
    });
}

void __cdecl se_stop(se_session* session) { delete session; }

int32_t __cdecl se_output(se_session* session, const wchar_t* endpoint,
    uint32_t enabled, float gain, uint32_t delay_ms) {
    if (!session || !valid_id(endpoint) || session->source == endpoint || enabled > 1 ||
        !std::isfinite(gain) || gain < 0 || gain > 1 || delay_ms > 500) return E_INVALIDARG;
    return guarded([&] {
        if (auto* output = session->find(endpoint)) {
            output->gain.store(gain);
            output->delay.store(delay_ms);
            output->enabled.store(enabled != 0);
            return S_OK;
        }
        if (!enabled) return S_OK;
        const auto index = session->count.load(std::memory_order_relaxed);
        if (index == max_outputs) return HRESULT_FROM_WIN32(ERROR_TOO_MANY_NAMES);
        auto output = std::make_unique<Output>(endpoint);
        output->gain.store(gain);
        output->delay.store(delay_ms);
        auto* instance = output.get();
        output->thread = std::thread([session, instance] { session->output_loop(*instance); });
        session->outputs[index] = std::move(output);
        session->count.store(index + 1, std::memory_order_release);
        return S_OK;
    });
}

int32_t __cdecl se_source_status(se_session* session, se_status* status) {
    if (!session || !status) return E_POINTER;
    *status = session->source_stats.read();
    return S_OK;
}

int32_t __cdecl se_output_status(se_session* session, const wchar_t* endpoint, se_status* status) {
    if (!session || !endpoint || !status) return E_POINTER;
    const auto* output = session->find(endpoint);
    if (!output) return HRESULT_FROM_WIN32(ERROR_NOT_FOUND);
    *status = output->stats.read();
    return S_OK;
}

int32_t __cdecl se_set_paused(se_session* session, uint32_t paused) {
    if (!session || paused > 1) return E_INVALIDARG;
    session->paused.store(paused != 0, std::memory_order_relaxed);
    return S_OK;
}

int32_t __cdecl se_source_signal(se_session* session, uint32_t* present) {
    if (!session || !present) return E_POINTER;
    const auto recent = session->last_signal.load(std::memory_order_relaxed);
    *present = recent && GetTickCount64() - recent <= 2500 ? 1u : 0u;
    return S_OK;
}

int32_t __cdecl se_test_output(se_session* session, const wchar_t* endpoint) {
    if (!session || !valid_id(endpoint)) return E_INVALIDARG;
    auto* output = session->find(endpoint);
    if (!output || !output->enabled.load() || output->stats.state.load() != SE_STREAMING ||
        session->paused.load() || output->gain.load() <= 0) return HRESULT_FROM_WIN32(ERROR_NOT_READY);
    output->test_before.store(GetTickCount64() + 350, std::memory_order_relaxed);
    return S_OK;
}
