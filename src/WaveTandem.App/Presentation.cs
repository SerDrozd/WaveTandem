using System.IO;
using System.Reflection;
using System.Text.Json;

namespace WaveTandem.App;

public abstract class Bindable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed([CallerMemberName] string? property = null) =>
        PropertyChanged?.Invoke(this, new(property));

    protected bool Set<T>(ref T target, T value, [CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(target, value)) return false;
        target = value;
        Changed(property);
        return true;
    }
}

public sealed class Localizer : Bindable
{
    private readonly Dictionary<string, string> english = Read("en");
    private Dictionary<string, string> current = [];
    public string this[string key] => current.GetValueOrDefault(key) ?? english.GetValueOrDefault(key) ?? key;

    public void Select(string code)
    {
        current = code == "en" ? english : Read(code);
        Changed("Item[]");
    }

    public string Format(string key, params object[] values) => string.Format(CultureInfo.CurrentCulture, this[key], values);

    private static Dictionary<string, string> Read(string code)
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"WaveTandem.App.Localization.{code}.json")
            ?? throw new InvalidOperationException($"Missing localization {code}");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
            ?? throw new InvalidOperationException($"Empty localization {code}");
    }
}

public sealed record Choice(string Code, string Label);

public sealed class DeviceRow : Bindable
{
    private Endpoint endpoint;
    private OutputProfile profile;
    private StreamStatus? status;
    private readonly Action<DeviceRow, OutputProfile> edited;

    public DeviceRow(Endpoint endpoint, OutputProfile profile, Localizer text, Action<DeviceRow, OutputProfile> edited)
    {
        this.endpoint = endpoint;
        this.profile = profile;
        this.edited = edited;
        Text = text;
    }

    public Localizer Text { get; }
    public string Id => endpoint.Id;
    public string Name => endpoint.Name;
    public bool IsActive => endpoint.IsActive;
    public bool IsDefault => endpoint.IsDefault;
    public OutputProfile Profile => profile;
    public string Connection => status?.State switch
    {
        StreamState.Streaming => Text["Receiving"],
        StreamState.Retrying => Text["Reconnecting"],
        StreamState.Connecting => Text["Connecting"],
        _ => IsActive ? Text["Available"] : Text["Unavailable"]
    };
    public string SourceLabel => IsDefault ? Text.Format("DefaultDevice", Name) : Name;
    public string FormatLabel => endpoint.SampleRate == 0 ? Text["FormatUnknown"] :
        Text.Format("DeviceFormat", endpoint.SampleRate, endpoint.Channels);
    public string Diagnostics => status is null ? FormatLabel : Text.Format("OutputMetrics",
        FormatLabel, status.LatencyMs, status.QueueMs, status.DriftPpm, status.Underruns,
        status.Overruns, status.Reconnects, status.Error);

    public bool Enabled { get => profile.Enabled; set => Edit(profile with { Enabled = value }); }
    public int Volume { get => profile.Volume; set => Edit(profile with { Volume = value }); }
    public bool Muted { get => profile.Muted; set => Edit(profile with { Muted = value }); }
    public int Delay { get => profile.DelayMs; set => Edit(profile with { DelayMs = value }); }

    public void Refresh(Endpoint value, StreamStatus? streamStatus)
    {
        if (endpoint == value && status == streamStatus) return;
        var previous = endpoint;
        endpoint = value;
        status = streamStatus;
        if (previous.Name != value.Name) Changed(nameof(Name));
        if (previous.Name != value.Name || previous.IsDefault != value.IsDefault) Changed(nameof(SourceLabel));
        Changed(nameof(Connection));
        Changed(nameof(Diagnostics));
    }

    public void Relocalize() => Changed(string.Empty);

    public void Restore(OutputProfile value)
    {
        profile = value;
        Changed(string.Empty);
    }

    private void Edit(OutputProfile value)
    {
        value = value.Normalize();
        if (profile == value) return;
        var previous = profile;
        profile = value;
        if (previous.Enabled != value.Enabled) Changed(nameof(Enabled));
        if (previous.Volume != value.Volume) Changed(nameof(Volume));
        if (previous.Muted != value.Muted) Changed(nameof(Muted));
        if (previous.DelayMs != value.DelayMs) Changed(nameof(Delay));
        edited(this, previous);
    }
}
