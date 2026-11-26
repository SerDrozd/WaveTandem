# Verification

```powershell
./scripts/check.ps1
./scripts/build.ps1
out/native/Release/endpoint_smoke.exe --capture
out/native/Release/audio_tests.exe --benchmark
```

The native tests check FIFO bounds, wraparound/concurrency, resampling, alias rejection at Nyquist for a 2:1 ratio, drift clamp, convergence with independent simulated clocks and units. The endpoint smoke checks the ABI, enumeration, validation, retry and disabled state with synthetic unavailable IDs. The capture flag verifies packets and timestamps on a real WASAPI loopback stream using silent playback on the first active output. The xUnit tests cover preferences, migration, invalid inputs, volume conversion and preservation of unreadable files.

On 26 November 2026, the Windows x64 Release build, two native CTest suites, ten xUnit cases, WASAPI packet capture and the WPF visual harness passed. Windows exposed one active Realtek playback endpoint. The multi-output hardware tests, acoustic calibration, and long-run hardware tests remain open.

## Playback and recovery

1. Connect two output endpoints. Play a click track on the first endpoint shown by enumeration. Run `out/native/Release/endpoint_smoke.exe --routing`. The test checks capture timestamps, an active secondary stream and rejection of a feedback route. It opens up to three additional outputs at 5% volume.
2. Repeat with at least three endpoints, including different formats or sample rates. Use the GUI to confirm that each copy is audible, mute and change one gain without changing the others, and compare timing with the [calibration guide](latency.md).
3. Disconnect one output and watch the others continue. Reconnect the original endpoint and confirm the reconnect counter changes once. Test a re-paired endpoint with a different ID and confirm that selecting it requires a user action.
4. Change the Windows default with a fixed source and verify the selected source remains pinned. Repeat with **Follow Windows default**, first switching to a previously selected listener. Check that the new source is excluded from the regenerated listener route. Pause copies, switch again, and verify that pause persists. Suspend and resume Windows. In a disposable environment, restart the Windows Audio service.
5. Stop and start repeatedly, including during device enumeration and recovery. Check close-to-tray, tray Stop and tray Exit.

## Stability

Run each proposed hardware pairing for at least an hour. Record Windows build, device IDs or sanitized names, codec/profile, formats, output count, elapsed time, CPU and working set from Task Manager, FIFO queue, gap and overflow deltas, drift and reconnect deltas. Capture the benchmark output alongside those measurements. The benchmark exercises DSP in-process and excludes driver, transport and acoustic delays.

## UI and release

For a Windows desktop with a playback endpoint, use the visual smoke harness to render the live application view at its default size and at a narrow size and to check localized resources.

The harness also runs a simulated routing scenario to cover source signal state, per-listener test signals, pause, default-output changes, report redaction, and saving and reopening a listening setup.

```powershell
$native = (Resolve-Path out/native/Release/WaveTandemAudio.dll).Path
dotnet run --project tests/WaveTandem.App.Smoke -c Release -p:NativeAudioPath="$native" -- out/visual
```

Use Tab and Shift+Tab, Narrator and keyboard control of sliders. Check high contrast, 100% and 150% DPI, light and dark themes, three languages, long device names, no devices and reconnect behavior. Verify the extracted ZIP launches on a clean x64 Windows installation and matches its SHA-256 file. A hardware checklist run should include its date and results before making a stability or device support claim.
