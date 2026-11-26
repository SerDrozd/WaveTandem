# WaveTandem

WaveTandem shares the sound already playing on a Windows output with other connected headphones, speakers, or audio interfaces. The main device keeps playing normally. Each additional listener gets independent volume, mute, and saved timing settings.

The app is a portable, driver-free Windows desktop program. Development began on 12 November 2026 under the working name SyncEar. The Git history begins on 26 November 2026.

![WaveTandem on first launch with one Windows playback device connected](docs/images/sharing-light.png)

[Dark theme at a narrower window size](docs/images/sharing-dark-uk.png).

## Quick start

1. Connect at least two playback devices in Windows and play audio on one of them.
2. Extract the entire Windows x64 archive and open `WaveTandem.exe`.
3. Choose the device that is already playing as the main output.
4. Select the other listeners and choose **Start sharing**.
5. Open **Show timing controls** to adjust extra delay on an earlier listener. Use **Diagnostics** to check queue, drift and reconnect information.

Use the x64 ZIP and checksum from [Releases](https://github.com/SerDrozd/SyncEar/releases) when a version is published. Verify the archive with `Get-FileHash .\WaveTandem-win-x64.zip -Algorithm SHA256` and compare it with the adjacent `.sha256` file. The archive includes the .NET desktop runtime. Builds are unsigned.

## Features

- One main output and up to 16 additional endpoint profiles per session.
- Per-listener volume, mute and 0–500 ms extra delay.
- Recovery for a previously selected endpoint that returns with the same Windows ID.
- Drift correction through adaptive resampling, with status and software delay estimates.
- English, Russian and Ukrainian, with Windows, light and dark appearances.
- Optional sharing from the notification area after closing the window.

## How it works

WASAPI loopback reads the selected endpoint's shared-mode playback mix. A C++20 engine converts it to stereo float and publishes frames to bounded queues. Each output drains its own queue at the rate of its audio device. A feedback controller adjusts the resampling ratio to keep the queue near its target. The WPF app uses a versioned native C ABI to configure listeners and display diagnostics. Read the [architecture](docs/architecture.md), [audio pipeline](docs/audio-pipeline.md), and [synchronization design](docs/synchronization.md).

## Compatibility

The target is x64 Windows 11, with the same WASAPI APIs available on Windows 10 22H2. USB, wired, Bluetooth Classic and Bluetooth LE Audio devices are usable when Windows exposes active playback endpoints and the PC can sustain their simultaneous playback. Compatibility depends on the adapter, drivers and devices. See the [hardware test matrix](docs/device-compatibility.md) for verification status.

If your Windows Quick Settings includes **Shared Audio** and both headsets support it, Windows can share directly using Bluetooth LE Audio broadcast. [Microsoft Shared Audio documentation](https://support.microsoft.com/en-au/servicing/os/windows-11/2026/05/may-26-2026-kb5089573-os-builds-26200-8524-and-26100-8524-preview).

## Build and test

Use PowerShell 7, .NET 10 SDK, CMake 3.24 or later, Visual Studio 2022 C++ Build Tools and a Windows 10/11 SDK. For Visual Studio 2026, pass `-Generator 'Visual Studio 18 2026'` with a version of CMake that supports it.

```powershell
./scripts/check.ps1
./scripts/build.ps1 -Package
```

The Release build uses MSVC warnings and static analysis. The script runs the native DSP and endpoint tests and the xUnit settings tests before creating the ZIP in `out/dist`. See [testing](docs/testing.md) for benchmarks, visual checks, hardware verification and long-run measurements.

## Settings, timing, and troubleshooting

Preferences live in `%LOCALAPPDATA%\WaveTandem\settings.json`. Profiles use Windows endpoint IDs, so plugging a device into a different port or pairing it again may require selecting its replacement. The app can keep the selected source after the Windows default changes. Route the player to that source, or stop and choose the new default output.

Software delay is estimated from queued frames. Bluetooth transport and acoustic latency vary by device. Use the [calibration guide](docs/latency.md) to tune devices. If the main device is the earliest audible device, the app cannot delay its direct Windows playback. Protected content and streams that bypass the shared mix may be absent from loopback. See [troubleshooting](docs/troubleshooting.md).

## Privacy and project information

Audio remains in memory on the PC. The app stores settings locally and uses no account, upload or telemetry. The most recent diagnostic events stay in memory during the run. See [security reporting](SECURITY.md), [contributing](CONTRIBUTING.md), the [roadmap](docs/roadmap.md), and the [MIT license](LICENSE).
