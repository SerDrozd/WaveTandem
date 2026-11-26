namespace WaveTandem.Core;

public sealed record Endpoint(string Id, string Name, uint State, uint FormFactor,
    bool IsDefault, uint SampleRate, uint Channels)
{
    public bool IsActive => State == 1;
}

public enum StreamState : uint { Disabled, Connecting, Streaming, Retrying }

public sealed record StreamStatus(StreamState State, int Error, ulong Underruns, ulong Overruns,
    ulong Reconnects, ulong Position, ulong Timestamp100Ns, float QueueMs, float LatencyMs,
    float DriftPpm, uint Discontinuities);

public sealed record AudioSnapshot(StreamStatus Source, IReadOnlyDictionary<string, StreamStatus> Outputs, bool HasSignal);

public sealed record OutputProfile(bool Enabled = false, int Volume = 80, bool Muted = false, int DelayMs = 0)
{
    public OutputProfile Normalize() => this with
    {
        Volume = Math.Clamp(Volume, 0, 100),
        DelayMs = Math.Clamp(DelayMs, 0, 500)
    };

    public float Gain => Muted ? 0 : Volume / 100f;
}

public sealed class AudioException(string operation, int code, Exception? inner = null)
    : Exception($"{operation} (0x{code:X8})", inner)
{
    public string Operation { get; } = operation;
    public int Code { get; } = code;
}

public interface IAudioEngine : IDisposable
{
    IReadOnlyList<Endpoint> Enumerate();
    void Start(string sourceId, IReadOnlyDictionary<string, OutputProfile> outputs);
    void Configure(string id, OutputProfile profile);
    void SetPaused(bool paused);
    void TestOutput(string id);
    AudioSnapshot? Snapshot();
    void Stop();
}
