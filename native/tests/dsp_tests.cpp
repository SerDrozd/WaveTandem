#include "dsp.h"
#include <chrono>
#include <cstdlib>
#include <iostream>
#include <string_view>
#include <thread>

using namespace se;

void expect(bool condition, const char* name) {
    if (condition) return;
    std::cerr << name << '\n';
    std::exit(1);
}

void benchmark() {
    constexpr uint32_t frames = 480;
    constexpr uint32_t periods = 2'000;
    std::array<Frame, frames> signal{};
    for (uint32_t i = 0; i < frames; ++i) {
        const auto sample = std::sin(static_cast<float>(i) * 0.1f);
        signal[i] = {sample, sample};
    }
    ResamplingKernel kernel;
    const double step = 44100.0 / 48000.0;
    kernel.configure(step);
    Ring fifo(16384);
    fifo.push(signal.data(), frames);
    Resampler resampler;
    std::array<Frame, frames> buffer{};
    float checksum = 0;
    const auto start = std::chrono::steady_clock::now();
    for (uint32_t period = 0; period < periods; ++period) {
        fifo.push(signal.data(), 441);
        resampler.render(fifo, frames, step, kernel, [&](uint32_t frame, Frame result) { buffer[frame] = result; });
        checksum += buffer[period % frames].right;
    }
    const auto elapsed = std::chrono::duration<double>(std::chrono::steady_clock::now() - start);
    std::cout << "44.1 to 48 kHz stereo, " << frames * periods << " output frames, "
              << elapsed.count() << " s CPU wall time, checksum " << checksum << '\n';
}

void check_independent_clocks(double ppm) {
    constexpr uint32_t rate = 48000;
    constexpr uint32_t target = 2880;
    Ring fifo(8192);
    ResamplingKernel kernel;
    kernel.configure(1);
    Resampler resampler;
    const std::array<Frame, target> silence{};
    fifo.push(silence.data(), target);
    double remainder = 0;
    for (uint32_t period = 0; period < 12'000; ++period) {
        remainder += 480 * (1 + ppm / 1'000'000.0);
        const auto frames = static_cast<uint32_t>(remainder);
        remainder -= frames;
        expect(fifo.push(silence.data(), frames) == frames, "drift did not overflow queue");
        resampler.adjust(milliseconds(fifo.available(), rate) - milliseconds(target, rate), 0.01f);
        expect(resampler.render(fifo, 480, 1, kernel, [](uint32_t, Frame) {}) == 480,
            "drift did not underrun queue");
    }
    expect(std::abs(resampler.drift_ppm() - ppm) < 20, "ratio converges to simulated clock");
    expect(std::abs(milliseconds(fifo.available(), rate) - 60) < 35, "drift keeps queue near target");
}

int main(int argc, char** argv) {
    if (argc == 2 && std::string_view(argv[1]) == "--benchmark") {
        benchmark();
        return 0;
    }
    Ring queue(4);
    const Frame packet[] = {{1, 1}, {2, 2}, {3, 3}, {4, 4}};
    expect(queue.push(packet, 4) == 4, "fill queue");
    expect(queue.push(packet, 1) == 0, "bounded overflow");
    queue.consume(3);
    expect(queue.push(packet, 3) == 3 && queue.peek(0).left == 4 && queue.peek(3).left == 3, "wrap order");
    queue.clear();
    expect(queue.available() == 0, "flush queue");
    Resampler resampler;
    expect(mono({1, -1}) == 0 && clean(2) == 1, "channel mapping and clipping");
    expect(target_frames(48000, 100, 60) == 7680, "delay target");
    expect(milliseconds(2400, 48000) == 50, "latency units");
    for (int i = 0; i < 200; ++i) resampler.adjust(300, 0.01f);
    expect(resampler.drift_ppm() > 1000 && resampler.drift_ppm() < 3000, "drift limit");
    resampler.adjust(-300, 2);
    expect(resampler.drift_ppm() == -3000, "drift direction");

    ResamplingKernel kernel;
    kernel.configure(2);
    Ring filter_input(128);
    Frame high_frequency[128];
    for (unsigned i = 0; i < 128; ++i) high_frequency[i] = {i % 2 == 0 ? 1.0f : -1.0f, 0.5f};
    filter_input.push(high_frequency, 128);
    resampler.reset();
    expect(resampler.render(filter_input, 36, 2, kernel, [](uint32_t, Frame frame) {
        expect(std::abs(frame.left) < 0.01f, "downsample rejects Nyquist");
        expect(std::abs(frame.right - 0.5f) < 0.001f, "filter preserves DC gain");
    }) == 36, "filtered lookahead");
    filter_input.clear();
    kernel.configure(0.5);
    filter_input.push(high_frequency, 32);
    resampler.reset();
    expect(resampler.render(filter_input, 5, 0.5, kernel, [](uint32_t, Frame frame) {
        expect(std::abs(frame.right - 0.5f) < 0.001f, "upsampling preserves DC gain");
    }) == 2, "upsampling holds back insufficient lookahead");

    check_independent_clocks(650);
    check_independent_clocks(-650);

    Ring concurrent(127);
    constexpr uint32_t frames = 200000;
    std::thread producer([&] {
        for (uint32_t i = 0; i < frames; ++i) {
            const Frame frame{static_cast<float>(i), 0};
            while (!concurrent.push(&frame, 1)) std::this_thread::yield();
        }
    });
    for (uint32_t i = 0; i < frames; ++i) {
        while (!concurrent.available()) std::this_thread::yield();
        expect(concurrent.peek(0).left == static_cast<float>(i), "concurrent queue order");
        concurrent.consume(1);
    }
    producer.join();
}
