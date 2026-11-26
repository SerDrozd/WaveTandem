using System.IO;
using WaveTandem.Audio.Interop;

namespace WaveTandem.App;

public partial class App : Application
{
    private Mutex? instance;
    private bool ownsInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        instance = new Mutex(initiallyOwned: true, @"Local\WaveTandem.Desktop", out ownsInstance);
        if (!ownsInstance)
        {
            Shutdown();
            return;
        }
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WaveTandem", "settings.json");
        var model = new MainViewModel(new AudioEngine(), new SettingsStore(path));
        MainWindow = new MainWindow(model);
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (ownsInstance) instance?.ReleaseMutex();
        instance?.Dispose();
        base.OnExit(e);
    }
}
