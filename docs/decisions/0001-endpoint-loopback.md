# ADR 0001 · Shared-mode endpoint loopback

Accepted.

Windows can expose multiple playback endpoints without providing a universal way to duplicate their mix. We capture one explicitly selected endpoint's shared-mode mix and render independently to extra endpoints. The audio tree and network stay local to the PC. The initial implementation uses Windows Core Audio directly through a small C ABI.

[Windows Shared Audio](https://support.microsoft.com/en-au/servicing/os/windows-11/2026/05/may-26-2026-kb5089573-os-builds-26200-8524-and-26100-8524-preview) uses LE Audio broadcast with supported hardware. [Audio Router](https://github.com/audiorouterdev/audio-router) focuses on assigning or duplicating individual application audio. [Voicemeeter](https://vb-audio.com/Voicemeeter/) offers a virtual mixing console with hardware outputs. [NAudio](https://github.com/naudio/NAudio) is a managed audio toolkit with WASAPI support. Direct Core Audio gives this app control over endpoint workers, timing queues and the interop surface with no additional audio library dependency.

Consequences include pinned-source semantics, limits on protected or exclusive audio, and per-device transport delays outside the app's control. Automatic acoustic calibration would require separate measurement. Process-specific loopback remains a separate future source option.
