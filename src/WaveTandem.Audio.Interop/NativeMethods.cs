using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using WaveTandem.Core;

namespace WaveTandem.Audio.Interop;

internal sealed class SessionHandle() : SafeHandleZeroOrMinusOneIsInvalid(ownsHandle: true)
{
    protected override bool ReleaseHandle()
    {
        NativeMethods.se_stop(handle);
        return true;
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeStatus
{
    internal StreamState State;
    internal int Error;
    internal ulong Underruns, Overruns, Reconnects, Position, Timestamp;
    internal float QueueMs, LatencyMs, DriftPpm;
    internal uint Discontinuities;

    internal readonly StreamStatus ToStatus() => new(State, Error, Underruns, Overruns, Reconnects,
        Position, Timestamp, QueueMs, LatencyMs, DriftPpm, Discontinuities);
}

internal static class NativeMethods
{
    private const string Library = "WaveTandemAudio.dll";

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    internal delegate void EndpointCallback(
        [MarshalAs(UnmanagedType.LPWStr)] string id, [MarshalAs(UnmanagedType.LPWStr)] string name,
        uint state, uint formFactor, uint isDefault, uint sampleRate, uint channels, nint context);

    [DllImport(Library, ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    internal static extern uint se_abi_version();

    [DllImport(Library, ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    internal static extern int se_enumerate(EndpointCallback callback, nint context);

    [DllImport(Library, ExactSpelling = true, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    internal static extern int se_start(string source, out SessionHandle session);

    [DllImport(Library, ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    internal static extern void se_stop(nint session);

    [DllImport(Library, ExactSpelling = true, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    internal static extern int se_output(SessionHandle session, string endpoint, uint enabled, float gain, uint delay);

    [DllImport(Library, ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    internal static extern int se_source_status(SessionHandle session, out NativeStatus status);

    [DllImport(Library, ExactSpelling = true, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    internal static extern int se_output_status(SessionHandle session, string endpoint, out NativeStatus status);
}
