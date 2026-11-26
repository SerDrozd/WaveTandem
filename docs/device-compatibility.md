# Device compatibility

WaveTandem works with active Windows playback endpoints rather than calling Bluetooth APIs. Windows must support playing to all selected endpoints at once. Adapter limits, driver problems, headset profiles, and radio congestion can affect that capability.

| Scenario | What to verify |
| --- | --- |
| Bluetooth Classic | Pair two headsets, verify both playback endpoints remain active, verify media quality while calls or microphones are inactive. |
| Bluetooth LE Audio | Use a compatible PC and LE Audio accessories. Check both the native Windows Shared Audio and WaveTandem paths separately. |
| USB playback | Test different ports and sample rates. Re-select an endpoint if Windows changes its ID. |
| Wired playback | Test analog and HDMI/DisplayPort outputs. Unplug and replug one listener while another keeps playing. |
| Mixed devices | Try a Bluetooth and USB listener at the same time. Measure any delay in each direction. |

The automated endpoint smoke test can run without headsets. The optional routing test needs at least two live endpoints and a player already sending audio. The project does not certify particular headsets, adapters or acoustic performance. See [testing](testing.md) for the procedure and [Microsoft's LE Audio requirements](https://support.microsoft.com/en-gb/windows/hardware/bluetooth/check-if-a-windows-11-device-supports-bluetooth-low-energy-audio) for system compatibility.
