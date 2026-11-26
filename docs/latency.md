# Timing and calibration

| Value | Meaning |
| --- | --- |
| Extra delay | User-selected 0–500 ms at one additional playback endpoint. Saved by Windows endpoint ID. |
| Software estimate | Current per-output FIFO delay plus the queued WASAPI render frames, converted using the two sample rates. |
| Physical latency | Time to hear the signal from the actual device. It includes hardware, drivers, codecs, and transport. Measure it acoustically. |

For an audible adjustment, play a click or metronome on the main output. Leave the latest device at its minimum delay. Add delay in small increments to the earliest additional listener and compare again. Repeat after changing Bluetooth profiles or reconnecting a headset. A microphone placed at comparable distances from both outputs can record the clicks for an external timing comparison. For calibrated results, document microphone placement and check repeatability.

Software estimates fluctuate with buffering and underruns. They are expected to be unavailable while a device is closed. Since the source is already playing through Windows, WaveTandem cannot delay its direct source path. On some equipment, a different device makes a better choice of main output.
