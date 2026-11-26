using WaveTandem.Core;

namespace WaveTandem.Audio.Interop;

public sealed class AudioEngine : IAudioEngine
{
    private readonly HashSet<string> configured = new(StringComparer.Ordinal);
    private SessionHandle? session;
    private bool verified;

    public IReadOnlyList<Endpoint> Enumerate()
    {
        VerifyLibrary();
        List<Endpoint> endpoints = [];
        Exception? callbackError = null;
        var result = NativeMethods.se_enumerate((id, name, state, form, isDefault, rate, channels, _) =>
        {
            try { endpoints.Add(new(id, name, state, form, isDefault != 0, rate, channels)); }
            catch (Exception error) { callbackError = error; }
        }, 0);
        if (callbackError is not null) throw new AudioException("enumeration", callbackError.HResult, callbackError);
        Check(result, "enumeration");
        return endpoints;
    }

    public void Start(string sourceId, IReadOnlyDictionary<string, OutputProfile> outputs)
    {
        VerifyLibrary();
        if (session is not null) throw new AudioException("start", unchecked((int)0x8007139F));
        int result = NativeMethods.se_start(sourceId, out var pending);
        try
        {
            Check(result, "start");
            foreach (var (id, profile) in outputs.Where(item => item.Value.Enabled))
            {
                Apply(pending, id, profile);
                configured.Add(id);
            }
            session = pending;
        }
        catch
        {
            pending?.Dispose();
            configured.Clear();
            throw;
        }
    }

    public void Configure(string id, OutputProfile profile)
    {
        if (session is null) return;
        Apply(session, id, profile);
        if (profile.Enabled) configured.Add(id);
        else configured.Remove(id);
    }

    public AudioSnapshot? Snapshot()
    {
        if (session is null) return null;
        Check(NativeMethods.se_source_status(session, out var source), "status");
        Dictionary<string, StreamStatus> outputs = new(StringComparer.Ordinal);
        foreach (string id in configured)
        {
            Check(NativeMethods.se_output_status(session, id, out var status), "status");
            outputs[id] = status.ToStatus();
        }
        Check(NativeMethods.se_source_signal(session, out var present), "signal");
        return new(source.ToStatus(), outputs, present != 0);
    }

    public void SetPaused(bool paused)
    {
        if (session is not null) Check(NativeMethods.se_set_paused(session, paused ? 1u : 0u), "pause");
    }

    public void TestOutput(string id)
    {
        if (session is not null) Check(NativeMethods.se_test_output(session, id), "test");
    }

    public void Stop()
    {
        session?.Dispose();
        session = null;
        configured.Clear();
    }

    public void Dispose() => Stop();

    private void VerifyLibrary()
    {
        if (verified) return;
        try
        {
            if (NativeMethods.se_abi_version() != 2)
                throw new AudioException("library", unchecked((int)0x8007000B));
        }
        catch (Exception error) when (error is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        {
            throw new AudioException("library", error.HResult, error);
        }
        verified = true;
    }

    private static void Apply(SessionHandle target, string id, OutputProfile profile)
    {
        var settings = profile.Normalize();
        Check(NativeMethods.se_output(target, id, settings.Enabled ? 1u : 0u, settings.Gain,
            (uint)settings.DelayMs), "output");
    }

    private static void Check(int result, string operation)
    {
        if (result < 0) throw new AudioException(operation, result);
    }
}
