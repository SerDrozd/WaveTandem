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
                if (model.Text["MainOutput"] != "Основной выход") throw new InvalidOperationException("Russian UI failed.");
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
                Console.WriteLine("Rendered the WPF views and verified managed WASAPI startup and shutdown.");
                result = 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); }
            finally { window.Close(); }
        });
        Dispatcher.Run();
        return result;
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
