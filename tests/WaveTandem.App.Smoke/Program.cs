using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WaveTandem.App;
using WaveTandem.Audio.Interop;
using WaveTandem.Core;

namespace WaveTandem.App.Smoke;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 1) return 2;
        var folder = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(folder);
        var app = new App();
        app.InitializeComponent();
        var settings = new SettingsStore(Path.Combine(folder, $"settings-{Guid.NewGuid():N}.json"));
        var model = new MainViewModel(new AudioEngine(), settings);
        var window = new MainWindow(model)
        {
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000,
            Top = -10000
        };
        int result = 1;
        window.Closed += (_, _) => Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
        window.Show();
        _ = window.Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                await model.RefreshAsync();
                await Task.Delay(250);
                if (model.Devices.Count == 0) throw new InvalidOperationException("Visual smoke needs a Windows audio endpoint.");
                model.Language = "en";
                model.Theme = "light";
                await Render(window, folder, "sharing-light.png");
                model.Language = "uk";
                model.Theme = "dark";
                model.ShowWelcome = false;
                window.Width = 540;
                window.Height = 490;
                await Render(window, folder, "sharing-dark-uk.png");
                if (model.Text["Start"] != "Почати трансляцію") throw new InvalidOperationException("Ukrainian UI failed.");
                model.Language = "ru";
                if (model.Text["MainOutput"] != "Источник звука") throw new InvalidOperationException("Russian UI failed.");
                await CheckRoutingWorkflow(folder);
                var source = model.Devices.FirstOrDefault(row => row.IsActive)
                    ?? throw new InvalidOperationException("WASAPI interop smoke needs an active endpoint.");
                await Task.Run(() =>
                {
                    using var interop = new AudioEngine();
                    interop.Start(source.Id, new Dictionary<string, OutputProfile>());
                    for (int retry = 0; retry < 30; retry++)
                    {
                        if (interop.Snapshot()?.Source.State == StreamState.Streaming) return;
                        System.Threading.Thread.Sleep(100);
                    }
                    throw new InvalidOperationException("Managed WASAPI loopback did not reach the streaming state.");
                });
                Console.WriteLine("Rendered the WPF views, verified route changes and saved setups, and exercised managed WASAPI startup and shutdown.");
                result = 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); }
            finally { window.Close(); }
        });
        Dispatcher.Run();
        return result;
    }

    private static async Task CheckRoutingWorkflow(string folder)
    {
        var engine = new ScenarioEngine();
        var store = new SettingsStore(Path.Combine(folder, $"route-{Guid.NewGuid():N}.json"));
        var model = new MainViewModel(engine, store);
        await model.RefreshAsync();
        model.FollowDefault = true;
        var headphones = model.Devices.Single(row => row.Id == "headphones");
        var speaker = model.Devices.Single(row => row.Id == "speaker");
        headphones.Enabled = true;
        headphones.Volume = 48;
        headphones.Delay = 80;
        speaker.Enabled = true;
        model.SetupName = "Movie night";
        model.SaveSetup();
        await model.ToggleAsync();
        Require(engine.Source == "laptop" && engine.OutputIds.Count == 2, "Initial mirrored route");
        Require(model.SignalLabel == model.Text["SignalSilent"], "Silence surfaced to the listener");
        engine.Signal = true;
        await model.RefreshAsync();
        Require(model.SignalLabel == model.Text["SignalPresent"], "Signal detected");
        await model.TestAsync(headphones);
        Require(engine.LastTest == headphones.Id, "Test targeted the requested listener");
        await model.TogglePauseAsync();
        Require(engine.Paused && headphones.Volume == 48 && !headphones.Muted, "Pause retained the device preferences");
        await model.TestAsync(speaker);
        Require(engine.LastTest == headphones.Id, "Pause prevented a test signal");
        engine.Default = "headphones";
        await model.RefreshAsync();
        Require(engine.Source == "headphones" && !engine.OutputIds.Contains("headphones") && engine.Paused,
            "Changed default excluded from mirrors and kept pause");
        engine.Default = null;
        await model.RefreshAsync();
        Require(model.Sharing && engine.Source is null, "Lost default stopped rendering while keeping session intent");
        engine.Default = "laptop";
        await model.RefreshAsync();
        Require(engine.Source == "laptop" && engine.OutputIds.Count == 2, "Default return restored listeners");
        await model.RefreshAsync();
        Require(!model.GetSupportReport().Contains("headphones", StringComparison.OrdinalIgnoreCase), "Support report removed device IDs");
        await model.ToggleAsync();
        headphones.Enabled = false;
        headphones.Volume = 1;
        model.LoadSetup();
        Require(headphones.Enabled && headphones.Volume == 48 && headphones.Delay == 80,
            "Saved setup restored enabled state, volume and delay");
        await model.CloseAsync();
        Require(store.Load().Preferences.Setups.Count == 1, "Saved setup persisted");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class ScenarioEngine : IAudioEngine
    {
        private static readonly StreamStatus Open = new(StreamState.Streaming, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        public string? Default { get; set; } = "laptop";
        public string? Source { get; private set; }
        public string? LastTest { get; private set; }
        public bool Paused { get; private set; }
        public bool Signal { get; set; }
        public HashSet<string> OutputIds { get; } = new(StringComparer.Ordinal);

        public IReadOnlyList<Endpoint> Enumerate() =>
            new[] { "laptop", "headphones", "speaker" }.Select(id => new Endpoint(id, id, 1, 0,
                id == Default, 48000, 2)).ToArray();

        public void Start(string sourceId, IReadOnlyDictionary<string, OutputProfile> outputs)
        {
            Require(Source is null && !outputs.ContainsKey(sourceId), "Source must be excluded from mirrors");
            Source = sourceId;
            OutputIds.UnionWith(outputs.Keys);
        }

        public void Configure(string id, OutputProfile profile)
        {
            if (profile.Enabled) OutputIds.Add(id);
            else OutputIds.Remove(id);
        }

        public void SetPaused(bool paused) => Paused = paused;
        public void TestOutput(string id) => LastTest = id;
        public AudioSnapshot? Snapshot() => Source is null ? null :
            new(Open, OutputIds.ToDictionary(id => id, _ => Open), Signal);

        public void Stop()
        {
            Source = null;
            Paused = false;
            OutputIds.Clear();
        }
        public void Dispose() => Stop();
    }

    private static async Task Render(Window window, string folder, string file)
    {
        await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ContextIdle);
        var surface = (FrameworkElement)window.Content;
        var dpi = VisualTreeHelper.GetDpi(surface);
        var image = new RenderTargetBitmap((int)Math.Ceiling(surface.ActualWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(surface.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY,
            PixelFormats.Pbgra32);
        image.Render(surface);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(image));
        using var output = File.Create(Path.Combine(folder, file));
        png.Save(output);
    }
}
