#include "dsp.h"
#include <cstdlib>
#include <iostream>
#include <thread>

using namespace se;

void expect(bool condition, const char* name) {
    if (condition) return;
    std::cerr << name << '\n';
    std::exit(1);
}

int main() {
    Ring queue(4);
    const Frame packet[] = {{1, 1}, {2, 2}, {3, 3}, {4, 4}};
    expect(queue.push(packet, 4) == 4, "fill queue");
    expect(queue.push(packet, 1) == 0, "bounded overflow");
    queue.consume(3);
    expect(queue.push(packet, 3) == 3 && queue.peek(0).left == 4 && queue.peek(3).left == 3, "wrap order");
    queue.clear();
    expect(queue.available() == 0, "flush queue");
    queue.push(packet, 4);
    Resampler resampler;
    float values[8]{};
    const auto produced = resampler.render(queue, 8, 0.5, [&](uint32_t i, Frame frame) { values[i] = frame.left; });
    expect(produced == 6 && values[1] == 1.5f && values[5] == 3.5f, "upsampling and boundary");
    queue.clear();
    queue.push(packet, 4);
    resampler.reset();
    expect(resampler.render(queue, 4, 2, [](uint32_t, Frame) {}) == 2, "downsample");
    expect(mono({1, -1}) == 0 && clean(2) == 1, "channel mapping and clipping");
    expect(target_frames(48000, 100, 60) == 7680, "delay target");
    expect(milliseconds(2400, 48000) == 50, "latency units");
    for (int i = 0; i < 200; ++i) resampler.adjust(300, 0.01f);
    expect(resampler.drift_ppm() > 1000 && resampler.drift_ppm() < 3000, "drift limit");
    resampler.adjust(-300, 2);
    expect(resampler.drift_ppm() == -3000, "drift direction");

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
