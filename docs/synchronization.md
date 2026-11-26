# Synchronization

The source and additional render endpoints have independent clocks. The renderer begins with a nominal input/output sample-rate ratio. Around that ratio, the controller uses the difference between queue size and target, in milliseconds. A low-pass update follows a proportional request of 35 ppm per millisecond, clamped to ±3,000 ppm. A growing queue raises consumption. The target is three render periods, with a 60–200 ms bound, plus the listener's selected delay. This favors a small, stable queue over minimum latency.

Increasing delay briefly pauses reading to fill the queue to the new target. Reducing it may drop queued frames. Loss of capture format, overflow and capture discontinuities cause a bounded flush and re-prime. Large queue excursions are trimmed instead of allowing lag to accumulate. The renderer emits silence when it exhausts the input.

Capture and render timestamps and positions are available through the ABI for diagnostics. The feedback loop primarily uses FIFO occupancy. It cannot recover absolute offset, device transport latency or acoustic phase from these clocks alone. The filter works best at ordinary audio sample-rate ratios. Large downsampling ratios need a longer or multistage filter for stronger alias suppression.

The [latency guide](latency.md) covers calibration and what the displayed estimate measures.
