# Changelog

## 0.2.0 development

- Reworked the sharing screen with a source signal indicator, direct access to Windows sound settings, and two neutral light and dark themes.
- Added default-output following with feedback prevention, pause for listener copies, and a brief test sound per connected listener.
- Added saved listening setups, Ctrl+Enter in the app window, and a support report without device names or identifiers.
- Rewrote the README around common listening and troubleshooting tasks.

## 0.1.0 development

- Added a Windows WPF desktop application in English, Russian and Ukrainian with device selection, individual gain and mute, per-device delay presets, diagnostics and notification-area controls.
- Added event-driven WASAPI loopback and independent render workers with bounded queues, adaptive resampling and endpoint reconnection.
- Added native and managed tests, Windows CI and tag-based packaging with SHA-256 checksums.
