# Troubleshooting

## No sound through the listeners

Play a known local file and check the signal next to the source selector. If WaveTandem detects silence, make sure the player uses that device in Windows. If it detects audio, make sure the listener is checked, connected, and audible. While sharing, use **Test sound** on that listener. If you hear the test, the output path is open. If the test is inaudible, check the device volume and whether Windows can play directly to it.

For Bluetooth, reconnect the headset and check that its media playback endpoint is active. Microphone use may switch the headset profile. If Windows creates a new endpoint when pairing again, select it in WaveTandem.

## Audio lags or skips

Open **Diagnostics** and check recent reconnects, gaps, overflow and queue depth. Remove a listener temporarily to test whether the Bluetooth adapter can sustain multiple streams. If one additional listener is early, use its **Extra delay** control. Read [calibration](latency.md) for the source-device limit.

## Audio engine or settings error

Extract the complete x64 ZIP before opening the app. The native DLL must be next to the executable. If Windows audio is unavailable across apps, check the Windows Audio service with the system administrator. For settings errors, close WaveTandem and copy the existing settings file before moving it aside to start with defaults.

Use **Copy support report** in Diagnostics to copy the app version, Windows version, stream states, error codes and queue counters. The copied report omits device names and identifiers. Add connection types and reproduction steps when opening an issue.
