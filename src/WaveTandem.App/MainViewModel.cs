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
    private bool busy, sharing, closing, dirty, refreshing, closeToTray, showWelcome, advanced, followDefault, paused;
    private string? issueCode;
    private int issueHresult;
    private AudioSnapshot? snapshot;
    private string? defaultId;
    private string? runningSourceId;
    private bool selectedInitialSource;
    private ListeningSetup? selectedSetup;
    private string setupName = "";
    private string? setupFeedback;

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
        followDefault = initial.FollowDefault;
        Setups = new(initial.Setups);
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
    public ObservableCollection<ListeningSetup> Setups { get; }
    public IReadOnlyList<Choice> Languages { get; } = [new("en", "English"), new("ru", "Русский"), new("uk", "Українська")];
    public IReadOnlyList<Choice> Themes => [new("light", Text["Light"]), new("dark", Text["Dark"])];
    public event Action? AppearanceChanged;
    public string ToggleLabel => Sharing ? Text["Stop"] : Text["Start"];
    public bool CanToggle => !busy && !closing && (Sharing || Source is { IsActive: true } && HasConnectedListener);
    public bool CanSelectSource => CanEditRoute && !FollowDefault;
    public bool CanEditRoute => !Sharing && !busy && !closing;
    public bool CanEdit => !busy && !closing;
    public bool Sharing => sharing;
    public bool CanPause => Sharing && runningSourceId is not null && CanEdit;
    public bool CanTestOutputs => CanPause && !paused;
    public string PauseLabel => Text[paused ? "ResumeCopies" : "PauseCopies"];
    public bool CanConfigure => Source is not null;
    public string Status => !Sharing ? Text["Ready"] :
        paused ? Text["PausedCopies"] :
        FollowDefault && runningSourceId is null ? Text["WaitingDefault"] : snapshot?.Source.State switch
    {
        StreamState.Retrying => Text["SourceLost"],
        StreamState.Streaming when ReceivingCount > 0 && snapshot.HasSignal => Text.Format("SharingCount", ReceivingCount),
        StreamState.Streaming when ReceivingCount > 0 => Text["WaitingSignal"],
        StreamState.Streaming => Text["WaitingOutputs"],
        _ => Text["Connecting"]
    };
    public string Notice => issueCode is not null ? Text.Format(issueCode, issueHresult) :
        settingsWarning ? Text["SettingsReadError"] :
        Source is null ? Text[FollowDefault ? "WaitingDefault" : "NoDevices"] :
        !Source.IsActive ? Text["SourceLost"] :
        Source.Id != defaultId && defaultId is not null ? Text["SourceDiffers"] :
        !Sharing && SelectedOutputCount == 0 ? Text["PickListener"] :
        !Sharing && !HasConnectedListener ? Text["ReconnectListener"] : "";
    public string SignalLabel => snapshot?.Source.State != StreamState.Streaming ? Text["SignalOff"] :
        Text[snapshot.HasSignal ? "SignalPresent" : "SignalSilent"];
    public string OutputCountLabel => Text.Format("SelectedCount", SelectedOutputCount);
    public string EmptyOutputs => Text[string.IsNullOrWhiteSpace(Filter) ? "NoOutputs" : "NoSearchResults"];
    public string SetupFeedback => setupFeedback is null ? "" : Text[setupFeedback];
    public string VersionLabel => $"WaveTandem {typeof(MainViewModel).Assembly.GetName().Version?.ToString(3)}";
    public bool HasSelectedSetup => SelectedSetup is not null;
    public string SourceDiagnostics => snapshot is null ? Text["Ready"] : Text.Format("SourceMetrics",
        snapshot.Source.State == StreamState.Streaming ? Text["Available"] : Status,
        snapshot.Source.Discontinuities, snapshot.Source.Reconnects, snapshot.Source.Error);
    public string RecentEvents => string.Join(Environment.NewLine, events);
    public bool NoOutputs => Outputs.IsEmpty;
    private int SelectedOutputCount => Devices.Count(row => row != Source && row.Enabled);
    private bool HasConnectedListener => Devices.Any(row => row != Source && row.Enabled && row.IsActive);
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

    public bool FollowDefault
    {
        get => followDefault;
        set
        {
            if (!CanEditRoute || !Set(ref followDefault, value)) return;
            if (value) Source = Devices.FirstOrDefault(row => row.IsDefault && row.IsActive);
            dirty = true;
            UpdateStatus();
        }
    }

    public string SetupName { get => setupName; set => Set(ref setupName, value); }
    public ListeningSetup? SelectedSetup
    {
        get => selectedSetup;
        set { if (Set(ref selectedSetup, value)) Changed(nameof(HasSelectedSetup)); }
    }

    public void SaveSetup()
    {
        if (!CanEditRoute) return;
        var name = SetupName.Trim();
        if (name.Length is 0 or > 40 || SelectedOutputCount is 0 or > 16)
        {
            SetSetupFeedback("SetupNeedsNameAndListeners");
            return;
        }
        var existing = Setups.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null || Setups.Count >= 12)
        {
            SetSetupFeedback(existing is null ? "SetupLimit" : "SetupNameInUse");
            return;
        }
        var outputs = Devices.Where(row => row != Source && row.Enabled)
            .ToDictionary(row => row.Id, row => new DevicePreference(row.Name, row.Profile), StringComparer.Ordinal);
        var setup = new ListeningSetup(name, FollowDefault, Source?.Id, Source?.Name, outputs);
        Setups.Add(setup);
        SelectedSetup = setup;
        SetupName = "";
        dirty = true;
        SetSetupFeedback("SetupSaved");
    }

    public void LoadSetup()
    {
        if (!CanEditRoute || SelectedSetup is not { } setup) return;
        FollowDefault = setup.FollowDefault;
        foreach (var row in Devices)
            row.Restore(setup.Outputs.GetValueOrDefault(row.Id)?.Profile ?? (row.Profile with { Enabled = false }));
        foreach (var (id, setting) in setup.Outputs)
            if (!Devices.Any(row => row.Id == id))
                Devices.Add(new(new(id, setting.Name, 4, uint.MaxValue, false, 0, 0), setting.Profile, Text, ApplyProfile));
        if (!FollowDefault && setup.SourceId is { } sourceId)
        {
            var selected = Devices.FirstOrDefault(row => row.Id == sourceId);
            if (selected is null)
            {
                selected = new(new(sourceId, setup.SourceName ?? Text["MainOutput"], 4, uint.MaxValue, false, 0, 0),
                    new(), Text, ApplyProfile);
                Devices.Add(selected);
            }
            Source = selected;
        }
        dirty = true;
        UpdateLists();
        UpdateStatus();
        SetSetupFeedback("SetupLoaded");
    }

    public void DeleteSetup()
    {
        if (!CanEditRoute || SelectedSetup is not { } setup) return;
        Setups.Remove(setup);
        SelectedSetup = null;
        dirty = true;
        SetSetupFeedback("SetupDeleted");
    }

    private void SetSetupFeedback(string key)
    {
        setupFeedback = key;
        Changed(nameof(SetupFeedback));
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
            if (value is not ("light" or "dark") || !Set(ref theme, value)) return;
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
        if (refreshing || closing || busy) return;
        refreshing = true;
        try
        {
            var (endpoints, current) = await RunAsync(() => (engine.Enumerate(), engine.Snapshot()));
            if (closing || busy) return;
            if (issueCode is "AudioAccess" or "LibraryError") issueCode = null;
            defaultId = endpoints.FirstOrDefault(device => device.IsDefault)?.Id;
            snapshot = Sharing ? current : null;
            Reconcile(endpoints);
            if (FollowDefault && Sharing && runningSourceId != Source?.Id)
                await FollowNewSourceAsync();
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
                paused = false;
                runningSourceId = null;
                snapshot = null;
                lastStates.Clear();
            }
            else if (Source is { } selected)
            {
                var outputs = SelectedOutputs(selected);
                await RunAsync(() => { engine.Start(selected.Id, outputs); return true; });
                runningSourceId = selected.Id;
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

    public async Task TogglePauseAsync()
    {
        if (!CanPause) return;
        busy = true;
        UpdateStatus();
        var next = !paused;
        try
        {
            await RunAsync(() => { engine.SetPaused(next); return true; });
            if (Sharing) paused = next;
        }
        catch (AudioException error) { Report("ActionError", error.Code); }
        finally
        {
            busy = false;
            UpdateStatus();
        }
    }

    public async Task TestAsync(DeviceRow row)
    {
        if (!CanTestOutputs || !row.CanTest || row == Source) return;
        try
        {
            await RunAsync(() => { engine.TestOutput(row.Id); return true; });
            if (issueCode == "TestUnavailable")
            {
                issueCode = null;
                UpdateStatus();
            }
        }
        catch (AudioException error) { Report("TestUnavailable", error.Code); }
    }

    public string GetSupportReport()
    {
        var text = new System.Text.StringBuilder();
        text.AppendLine($"WaveTandem {typeof(MainViewModel).Assembly.GetName().Version}");
        text.AppendLine($"Windows {Environment.OSVersion.Version}; sharing={Sharing}; paused={paused}");
        if (snapshot is not { } current) return text.ToString();
        text.AppendLine($"Source: {current.Source.State}; signal={current.HasSignal}; HRESULT=0x{current.Source.Error:X8}");
        var index = 0;
        foreach (var output in current.Outputs.Values)
            text.AppendLine(FormattableString.Invariant($"Output {++index}: {output.State}; queue={output.QueueMs:F1} ms; gaps={output.Underruns}; overflows={output.Overruns}; reconnects={output.Reconnects}; HRESULT=0x{output.Error:X8}"));
        return text.ToString();
    }

    private Dictionary<string, OutputProfile> SelectedOutputs(DeviceRow selected) =>
        Devices.Where(row => row != selected && row.Enabled)
            .ToDictionary(row => row.Id, row => row.Profile, StringComparer.Ordinal);

    private async Task FollowNewSourceAsync()
    {
        busy = true;
        snapshot = null;
        lastStates.Clear();
        UpdateStatus();
        var next = Source;
        var outputs = next is null ? null : SelectedOutputs(next);
        try
        {
            await RunAsync(() =>
            {
                engine.Stop();
                if (next is not null && outputs is not null)
                {
                    engine.Start(next.Id, outputs);
                    engine.SetPaused(paused);
                }
                return true;
            });
            runningSourceId = next?.Id;
        }
        catch (AudioException error)
        {
            runningSourceId = null;
            Report("ActionError", error.Code);
        }
        finally { busy = false; }
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
        if (!Sharing || runningSourceId is null || row.Id == runningSourceId || closing || busy) return;
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
        if (FollowDefault)
        {
            selectedInitialSource = true;
            Source = Devices.FirstOrDefault(row => row.IsDefault && row.IsActive);
        }
        else if (!selectedInitialSource && Devices.Count > 0)
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
            FollowDefault = FollowDefault,
            Setups = [.. Setups],
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
        Changed(nameof(EmptyOutputs));
    }

    private void UpdateStatus()
    {
        Changed(nameof(Sharing));
        Changed(nameof(ToggleLabel));
        Changed(nameof(CanToggle));
        Changed(nameof(CanSelectSource));
        Changed(nameof(CanEditRoute));
        Changed(nameof(CanPause));
        Changed(nameof(CanTestOutputs));
        Changed(nameof(PauseLabel));
        Changed(nameof(CanConfigure));
        Changed(nameof(CanEdit));
        Changed(nameof(Status));
        Changed(nameof(Notice));
        Changed(nameof(SourceDiagnostics));
        Changed(nameof(SignalLabel));
        Changed(nameof(OutputCountLabel));
        Changed(nameof(EmptyOutputs));
        Changed(nameof(SetupFeedback));
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
