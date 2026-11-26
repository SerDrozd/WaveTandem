#pragma once
#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstdint>
#include <vector>

namespace se {
struct Frame {
    float left = 0;
    float right = 0;
};

inline float clean(float value) noexcept {
    return std::isfinite(value) ? std::clamp(value, -1.0f, 1.0f) : 0.0f;
}

inline Frame interpolate(Frame first, Frame second, float phase) noexcept {
    return {std::lerp(first.left, second.left, phase), std::lerp(first.right, second.right, phase)};
}

inline float mono(Frame frame) noexcept {
    return (frame.left + frame.right) * 0.5f;
}

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

inline uint32_t target_frames(uint32_t rate, uint32_t delay_ms, uint32_t base_ms) noexcept {
    return rate * (delay_ms + base_ms) / 1000;
}

inline float milliseconds(uint32_t frames, uint32_t rate) noexcept {
    return static_cast<float>(frames) * 1000.0f / static_cast<float>(rate);
}

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
    uint32_t render(Ring& input, uint32_t count, double nominal_step, Sink&& sink) noexcept {
        const double step = nominal_step * (1.0 + static_cast<double>(drift_) / 1'000'000.0);
        const auto ready = input.available();
        uint32_t produced = 0;
        uint32_t consumed = 0;
        while (produced < count) {
            const auto advance = static_cast<uint32_t>(phase_ + step);
            if (ready - consumed < std::max(2u, advance)) break;
            sink(produced++, interpolate(input.peek(consumed), input.peek(consumed + 1), static_cast<float>(phase_)));
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
