namespace WaveTandem.App;

public sealed class MainViewModel : Bindable
{
    private readonly IAudioEngine engine;
    private readonly SettingsStore store;
    private readonly SemaphoreSlim operations = new(1);
    private readonly Queue<string> events = new();
    private readonly bool canSave;
    private readonly bool settingsWarning;
    private Preferences initial;
    private DeviceRow? source;
    private string filter = "", theme, language;
    private bool busy, sharing, closing, dirty, refreshing, closeToTray, showWelcome, advanced;
    private string? issueCode;
    private int issueHresult;
    private AudioSnapshot? snapshot;
    private string? defaultId;
    private bool selectedInitialSource;

    public MainViewModel(IAudioEngine engine, SettingsStore store)
    {
        this.engine = engine;
        this.store = store;
        var loaded = store.Load();
        initial = loaded.Preferences;
        canSave = loaded.CanSave;
        settingsWarning = loaded.Error is not null;
        theme = initial.Theme;
        language = initial.Language;
        closeToTray = initial.CloseToTray;
        showWelcome = !initial.HasSeenWelcome;
        Text.Select(language);
        Outputs = new ListCollectionView(Devices) { Filter = item =>
            item is DeviceRow row && row != Source && (row.IsActive || row.Enabled) &&
            (string.IsNullOrWhiteSpace(Filter) || row.Name.Contains(Filter.Trim(), StringComparison.CurrentCultureIgnoreCase))
        };
        Diagnostics = new ListCollectionView(Devices) { Filter = item => item is DeviceRow row && row != Source && row.Enabled };
    }

    public Localizer Text { get; } = new();
    public ObservableCollection<DeviceRow> Devices { get; } = [];
    public ICollectionView Outputs { get; }
    public ICollectionView Diagnostics { get; }
    public IReadOnlyList<Choice> Languages { get; } = [new("en", "English"), new("ru", "Русский"), new("uk", "Українська")];
    public IReadOnlyList<Choice> Themes => [new("system", Text["System"]), new("light", Text["Light"]), new("dark", Text["Dark"])];
    public event Action? AppearanceChanged;
    public string ToggleLabel => Sharing ? Text["Stop"] : Text["Start"];
    public bool CanToggle => !busy && !closing && (Sharing || Source is { IsActive: true } &&
        Devices.Any(row => row != Source && row.Enabled && row.IsActive));
    public bool CanSelectSource => !Sharing && !busy;
    public bool CanEdit => !busy && !closing;
    public bool Sharing => sharing;
    public bool CanConfigure => Source is not null;
    public string Status => !Sharing ? Text["Ready"] : snapshot?.Source.State switch
    {
        StreamState.Retrying => Text["SourceLost"],
        StreamState.Streaming when ReceivingCount > 0 => Text.Format("SharingCount", ReceivingCount),
        StreamState.Streaming => Text["WaitingOutputs"],
        _ => Text["Connecting"]
    };
    public string Notice => issueCode is not null ? Text.Format(issueCode, issueHresult) :
        settingsWarning ? Text["SettingsReadError"] :
        Source is null ? Text["NoDevices"] :
        !Source.IsActive ? Text["SourceLost"] :
        Source.Id != defaultId && defaultId is not null ? Text["SourceDiffers"] : "";
    public string SourceDiagnostics => snapshot is null ? Text["Ready"] : Text.Format("SourceMetrics",
        snapshot.Source.State == StreamState.Streaming ? Text["Available"] : Status,
        snapshot.Source.Discontinuities, snapshot.Source.Reconnects, snapshot.Source.Error);
    public string RecentEvents => string.Join(Environment.NewLine, events);
    public bool NoOutputs => Outputs.IsEmpty;
    private int ReceivingCount => snapshot?.Outputs.Values.Count(status => status.State == StreamState.Streaming) ?? 0;

    public DeviceRow? Source
    {
        get => source;
        set
        {
            if (!Set(ref source, value)) return;
            dirty = true;
            UpdateLists();
            UpdateStatus();
        }
    }

    public string Filter
    {
        get => filter;
        set { if (Set(ref filter, value)) UpdateLists(); }
    }

    public bool Advanced { get => advanced; set => Set(ref advanced, value); }

    public string Language
    {
        get => language;
        set
        {
            if (value is not ("en" or "ru" or "uk") || !Set(ref language, value)) return;
            Text.Select(value);
            foreach (var row in Devices) row.Relocalize();
            Changed(nameof(Themes));
            Changed(nameof(Theme));
            dirty = true;
            UpdateStatus();
            AppearanceChanged?.Invoke();
        }
    }

    public string Theme
    {
        get => theme;
        set
        {
            if (value is not ("system" or "light" or "dark") || !Set(ref theme, value)) return;
            dirty = true;
            AppearanceChanged?.Invoke();
        }
    }

    public bool CloseToTray
    {
        get => closeToTray;
        set { if (Set(ref closeToTray, value)) dirty = true; }
    }

    public bool ShowWelcome
    {
        get => showWelcome;
        set { if (Set(ref showWelcome, value)) dirty = true; }
    }

    public async Task RefreshAsync()
    {
        if (refreshing || closing) return;
        refreshing = true;
        try
        {
            var (endpoints, current) = await RunAsync(() => (engine.Enumerate(), engine.Snapshot()));
            if (closing) return;
            if (issueCode is "AudioAccess" or "LibraryError") issueCode = null;
            defaultId = endpoints.FirstOrDefault(device => device.IsDefault)?.Id;
            snapshot = Sharing ? current : null;
            Reconcile(endpoints);
            RecordTransitions(snapshot);
            UpdateStatus();
            await SaveIfDirtyAsync();
        }
        catch (AudioException error)
        {
            Report(error.Operation == "library" ? "LibraryError" : "AudioAccess", error.Code);
        }
        finally { refreshing = false; }
    }

    public async Task ToggleAsync()
    {
        if (!CanToggle) return;
        busy = true;
        issueCode = null;
        UpdateStatus();
        try
        {
            if (Sharing)
            {
                await RunAsync(() => { engine.Stop(); return true; });
                sharing = false;
                snapshot = null;
                lastStates.Clear();
            }
            else if (Source is { } selected)
            {
                var outputs = Devices.Where(row => row != selected && row.Enabled)
                    .ToDictionary(row => row.Id, row => row.Profile, StringComparer.Ordinal);
                await RunAsync(() => { engine.Start(selected.Id, outputs); return true; });
                sharing = true;
            }
        }
        catch (AudioException error) { Report("ActionError", error.Code); }
        finally
        {
            busy = false;
            UpdateStatus();
        }
        await RefreshAsync();
    }

    public async Task CloseAsync()
    {
        if (closing) return;
        closing = true;
        await RunAsync(() => { engine.Dispose(); return true; });
        await SaveIfDirtyAsync();
    }

    private async void ApplyProfile(DeviceRow row, OutputProfile previous)
    {
        dirty = true;
        if (previous.Enabled != row.Enabled)
        {
            if (!row.IsActive) Outputs.Refresh();
            Diagnostics.Refresh();
            Changed(nameof(NoOutputs));
        }
        UpdateStatus();
        if (!Sharing || row == Source || closing) return;
        var next = row.Profile;
        try
        {
            await RunAsync(() => { engine.Configure(row.Id, next); return true; });
            if (issueCode == "ActionError") issueCode = null;
            UpdateStatus();
        }
        catch (AudioException error)
        {
            if (row.Profile == next && row.Enabled && error.Code == unchecked((int)0x80070044))
                row.Restore(next with { Enabled = false });
            Report("ActionError", error.Code);
            UpdateLists();
        }
    }

    private void Reconcile(IReadOnlyList<Endpoint> endpoints)
    {
        var observed = endpoints.ToDictionary(endpoint => endpoint.Id, StringComparer.Ordinal);
        bool listChanged = false;
        foreach (var (id, value) in initial.Outputs)
            observed.TryAdd(id, new(id, value.Name, 4, uint.MaxValue, false, 0, 0));
        if (!selectedInitialSource && initial.SourceId is { } savedSource)
            observed.TryAdd(savedSource, new(savedSource, initial.SourceName ?? Text["MainOutput"], 4, uint.MaxValue, false, 0, 0));
        foreach (var row in Devices)
            if (row.Enabled || row == Source)
                observed.TryAdd(row.Id, new(row.Id, row.Name, 4, uint.MaxValue, false, 0, 0));
        foreach (var row in Devices.Where(row => !observed.ContainsKey(row.Id)).ToArray())
        {
            Devices.Remove(row);
            listChanged = true;
        }
        var existing = Devices.ToDictionary(row => row.Id, StringComparer.Ordinal);
        foreach (var endpoint in observed.Values.OrderByDescending(device => device.IsActive)
                     .ThenBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            StreamStatus? stream = snapshot?.Outputs.GetValueOrDefault(endpoint.Id);
            if (existing.TryGetValue(endpoint.Id, out var row))
            {
                listChanged |= row.IsActive != endpoint.IsActive || row.Name != endpoint.Name;
                row.Refresh(endpoint, stream);
            }
            else
            {
                Devices.Add(new(endpoint, initial.Outputs.GetValueOrDefault(endpoint.Id)?.Profile ?? new(), Text, ApplyProfile));
                listChanged = true;
            }
        }
        if (!selectedInitialSource && Devices.Count > 0)
        {
            selectedInitialSource = true;
            Source = Devices.FirstOrDefault(row => row.Id == initial.SourceId)
                ?? Devices.FirstOrDefault(row => row.IsDefault)
                ?? Devices.FirstOrDefault(row => row.IsActive);
        }
        if (listChanged) UpdateLists();
    }

    private readonly Dictionary<string, (StreamState State, int Error)> lastStates = new(StringComparer.Ordinal);

    private void RecordTransitions(AudioSnapshot? current)
    {
        if (current is null) return;
        Record("$source", current.Source, Text["MainOutput"]);
        foreach (var (id, status) in current.Outputs)
            Record(id, status, Devices.FirstOrDefault(row => row.Id == id)?.Name ?? Text["Listener"]);
        foreach (var key in lastStates.Keys.Where(key => key != "$source" && !current.Outputs.ContainsKey(key)).ToArray())
            lastStates.Remove(key);
    }

    private void Record(string id, StreamStatus value, string label)
    {
        var state = (value.State, value.Error);
        if (lastStates.TryGetValue(id, out var previous) && previous == state) return;
        lastStates[id] = state;
        AddEvent(Text.Format("StreamEvent", label, Text[value.State.ToString()], value.Error));
    }

    private async Task SaveIfDirtyAsync()
    {
        if (!dirty || !canSave) return;
        dirty = false;
        var preferences = new Preferences
        {
            Language = Language,
            Theme = Theme,
            CloseToTray = CloseToTray,
            HasSeenWelcome = !ShowWelcome,
            SourceId = Source?.Id ?? initial.SourceId,
            SourceName = Source?.Name ?? initial.SourceName,
            Outputs = Devices.Where(row => row.Profile != new OutputProfile())
                .ToDictionary(row => row.Id, row => new DevicePreference(row.Name, row.Profile), StringComparer.Ordinal)
        };
        try
        {
            await RunAsync(() => { store.Save(preferences); return true; });
            initial = preferences;
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or System.IO.InvalidDataException)
        {
            dirty = true;
            Report("SettingsWriteError", error.HResult);
        }
    }

    private async Task<T> RunAsync<T>(Func<T> call)
    {
        await operations.WaitAsync();
        try { return await Task.Run(call); }
        finally { operations.Release(); }
    }

    private void UpdateLists()
    {
        Outputs.Refresh();
        Diagnostics.Refresh();
        Changed(nameof(NoOutputs));
    }

    private void UpdateStatus()
    {
        Changed(nameof(Sharing));
        Changed(nameof(ToggleLabel));
        Changed(nameof(CanToggle));
        Changed(nameof(CanSelectSource));
        Changed(nameof(CanConfigure));
        Changed(nameof(CanEdit));
        Changed(nameof(Status));
        Changed(nameof(Notice));
        Changed(nameof(SourceDiagnostics));
    }

    private void Report(string key, int code)
    {
        if (issueCode != key || issueHresult != code) AddEvent(Text.Format(key, code));
        issueCode = key;
        issueHresult = code;
        UpdateStatus();
    }

    private void AddEvent(string message)
    {
        if (events.Count >= 128) events.Dequeue();
        events.Enqueue($"{DateTimeOffset.Now:t}  {message}");
        Changed(nameof(RecentEvents));
    }
}
