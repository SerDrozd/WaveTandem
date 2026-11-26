# Troubleshooting

## No sound through the listeners

Play a known local file. Confirm it reaches the main output in Windows and make sure the listener is checked, connected, and unmuted. If Windows uses another default output, use Windows Sound settings to route the player to the selected main output. Verify Windows can play directly to each listener.

For Bluetooth, reconnect the headset and check that its media playback endpoint is active. Microphone use may switch the headset profile. If Windows creates a new endpoint when pairing again, select it in WaveTandem.

## Audio lags or skips

Open **Diagnostics** and check recent reconnects, gaps, overflow and queue depth. Remove a listener temporarily to test whether the Bluetooth adapter can sustain multiple streams. If one additional listener is early, use its **Extra delay** control. Read [calibration](latency.md) for the source-device limit.

## Audio engine or settings error

Extract the complete x64 ZIP before opening the app. The native DLL must be next to the executable. If Windows audio is unavailable across apps, check the Windows Audio service with the system administrator. For settings errors, close WaveTandem and copy the existing settings file before moving it aside to start with defaults.

When opening an issue, include the app and Windows versions, endpoint types, repro steps and any HRESULT displayed under diagnostics. Remove personal device names before sharing logs.
