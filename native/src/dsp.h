#pragma once
#include <algorithm>
#include <array>
#include <atomic>
#include <cmath>
#include <cstdint>
#include <numbers>
#include <vector>

namespace se {
struct Frame {
    float left = 0;
    float right = 0;
};

inline float clean(float value) noexcept {
    return std::isfinite(value) ? std::clamp(value, -1.0f, 1.0f) : 0.0f;
}

inline float mono(Frame frame) noexcept {
    return (frame.left + frame.right) * 0.5f;
}

class TestTone {
public:
    explicit TestTone(uint32_t rate) noexcept : rate_(rate), position_(rate * 350 / 1000) {}
    void start() noexcept { position_ = 0; }
    bool active() const noexcept { return position_ < rate_ * 350 / 1000; }
    float next() noexcept {
        const auto position = position_++;
        const auto length = rate_ / 8;
        const auto second = rate_ * 225 / 1000;
        const auto offset = position >= second ? position - second : position;
        if (offset >= length) return 0;
        const auto fade = std::min({offset, length - offset - 1, rate_ / 125});
        const float envelope = static_cast<float>(fade) / static_cast<float>(rate_ / 125);
        const double phase = 2 * std::numbers::pi * 660 * offset / rate_;
        return 0.07f * envelope * static_cast<float>(std::sin(phase));
    }

private:
    uint32_t rate_;
    uint32_t position_;
};

#ifdef _MSC_VER
#pragma warning(push)
#pragma warning(disable: 4324) // Keep the two cursors on independent cache lines.
#endif
// The producer owns write_. The consumer owns read_. Neither waits for the other.
class Ring {
public:
    explicit Ring(uint32_t capacity) : frames_(capacity), capacity_(capacity) {}

    uint32_t push(const Frame* frames, uint32_t count) noexcept {
        const auto write = write_.load(std::memory_order_relaxed);
        const auto read = read_.load(std::memory_order_acquire);
        const auto accepted = std::min(count, capacity_ - static_cast<uint32_t>(write - read));
        const auto index = static_cast<uint32_t>(write % capacity_);
        const auto head = std::min(accepted, capacity_ - index);
        std::copy_n(frames, head, frames_.data() + index);
        std::copy_n(frames + head, accepted - head, frames_.data());
        write_.store(write + accepted, std::memory_order_release);
        return accepted;
    }

    uint32_t available() const noexcept {
        return static_cast<uint32_t>(write_.load(std::memory_order_acquire) -
            read_.load(std::memory_order_relaxed));
    }

    Frame peek(uint32_t offset) const noexcept {
        return frames_[(read_.load(std::memory_order_relaxed) + offset) % capacity_];
    }

    void consume(uint32_t count) noexcept {
        read_.store(read_.load(std::memory_order_relaxed) + count, std::memory_order_release);
    }

    void clear() noexcept { consume(available()); }

private:
    std::vector<Frame> frames_;
    uint32_t capacity_;
    alignas(64) std::atomic<uint64_t> write_{0};
    alignas(64) std::atomic<uint64_t> read_{0};
};
#ifdef _MSC_VER
#pragma warning(pop)
#endif

inline uint32_t target_frames(uint32_t rate, uint32_t delay_ms, uint32_t base_ms) noexcept {
    return rate * (delay_ms + base_ms) / 1000;
}

inline float milliseconds(uint32_t frames, uint32_t rate) noexcept {
    return static_cast<float>(frames) * 1000.0f / static_cast<float>(rate);
}

class ResamplingKernel {
public:
    void configure(double nominal_step) noexcept {
        // Leave room for the drift controller and the filter's transition band.
        const double cutoff = 0.92 / std::max(1.0, nominal_step * 1.003);
        for (size_t phase = 0; phase < coefficients_.size(); ++phase) {
            auto& row = coefficients_[phase];
            const double fraction = static_cast<double>(phase) / static_cast<double>(coefficients_.size() - 1);
            double sum = 0;
            for (size_t tap = 0; tap < row.size(); ++tap) {
                const double distance = static_cast<double>(tap) - static_cast<double>(row.size() / 2 - 1) - fraction;
                const double angle = std::numbers::pi * cutoff * distance;
                const double sinc = std::abs(angle) < 1e-9 ? 1 : std::sin(angle) / angle;
                const double window = 0.5 - 0.5 * std::cos(2 * std::numbers::pi *
                    static_cast<double>(tap) / static_cast<double>(row.size() - 1));
                row[tap] = static_cast<float>(sinc * window);
                sum += row[tap];
            }
            for (auto& coefficient : row) coefficient = static_cast<float>(coefficient / sum);
        }
    }

    static constexpr uint32_t taps = 32;

    Frame filter(const Ring& input, uint32_t offset, double phase) const noexcept {
        const auto index = static_cast<size_t>(phase * static_cast<double>(coefficients_.size() - 1) + 0.5);
        const auto& row = coefficients_[index];
        Frame result;
        for (uint32_t tap = 0; tap < taps; ++tap) {
            const auto frame = input.peek(offset + tap);
            result.left += frame.left * row[tap];
            result.right += frame.right * row[tap];
        }
        return result;
    }

private:
    std::array<std::array<float, taps>, 129> coefficients_{};
};

class Resampler {
public:
    void reset() noexcept {
        phase_ = 0;
        drift_ = 0;
    }

    void adjust(float error_ms, float elapsed_seconds) noexcept {
        // A slow, bounded feedback loop tolerates normal packet-sized FIFO oscillation.
        const float desired = std::clamp(error_ms * 35.0f, -3000.0f, 3000.0f);
        drift_ = std::lerp(drift_, desired, std::min(elapsed_seconds * 0.5f, 1.0f));
    }

    float drift_ppm() const noexcept { return drift_; }

    template<class Sink>
    uint32_t render(Ring& input, uint32_t count, double nominal_step, const ResamplingKernel& kernel,
        Sink&& sink) noexcept {
        const double step = nominal_step * (1.0 + static_cast<double>(drift_) / 1'000'000.0);
        const auto ready = input.available();
        uint32_t produced = 0;
        uint32_t consumed = 0;
        while (produced < count) {
            const auto advance = static_cast<uint32_t>(phase_ + step);
            if (ready - consumed < std::max(ResamplingKernel::taps, advance)) break;
            sink(produced++, kernel.filter(input, consumed, phase_));
            consumed += advance;
            phase_ += step - advance;
        }
        input.consume(consumed);
        return produced;
    }

private:
    double phase_ = 0;
    float drift_ = 0;
};
}
