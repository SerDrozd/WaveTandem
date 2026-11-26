# Audio pipeline

1. Enumerate render endpoints, including Windows device state, friendly name, form factor, default classification and active shared mix formats. IDs are opaque and stable for any one endpoint registration.
2. Open the selected source with event-driven WASAPI shared-mode loopback. Read packets, honor silent and discontinuity flags, and record the last valid capture position and QPC timestamp. Release each packet after copying it into bounded queues.
3. Convert Windows PCM (8, 16, packed 24 or 32-bit) and IEEE float mix samples into float stereo. Duplicate mono and mix a mapped front-center channel into stereo. Other surround channels are omitted. The render path writes mono or front L/R and silences other output channels.
4. Resample each queue to the sink sample rate using a 32-tap windowed-sinc filter with fractional-phase coefficients. Apply a separate gain or mute. Each WASAPI render client writes its own native format.
5. Read render padding and clock position where available. Keep underrun, overflow and reconnect counters. A shortfall is filled with silence and the queue primes again.

The source indicator reports recent audio above the engine's silence threshold. It reads the same frames already used for sharing. **Pause copies** sets the render gain to zero while the queues continue advancing. A listener's **Test sound** replaces its copy with two brief, low-level pulses while the other render streams continue normally. A test requires a connected, unmuted listener and active sharing.

The FIFO has 524,288 stereo frames per registered listener. Queues are provisioned on the control path and never grow. All clients use the system's normal shared-mode period. COM setup, kernel coefficient generation on format changes and endpoint recovery are outside normal packet processing. There are no blocking synchronization primitives or allocations in steady-state packet transfer.

The loopback stream includes the shared mix on one selected device. Protected sources can be suppressed by Windows. Read [WASAPI loopback recording](https://learn.microsoft.com/en-us/windows/win32/coreaudio/loopback-recording) and [event-driven buffering](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudioclient-initialize) for API behavior.
