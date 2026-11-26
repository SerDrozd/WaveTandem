using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace WaveTandem.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel model;
    private readonly DispatcherTimer refresh = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly Forms.NotifyIcon tray;
    private readonly Forms.ToolStripMenuItem openItem = new(), toggleItem = new(), exitItem = new();
    private bool allowClose, exiting, shuttingDown;

    public MainWindow(MainViewModel model)
    {
        this.model = model;
        InitializeComponent();
        DataContext = model;
        var menu = new Forms.ContextMenuStrip();
        menu.Items.AddRange([openItem, toggleItem, exitItem]);
        openItem.Click += (_, _) => ShowFromTray();
        toggleItem.Click += async (_, _) => await model.ToggleAsync();
        exitItem.Click += (_, _) => { exiting = true; Close(); };
        menu.Opening += (_, _) => UpdateTray();
        tray = new Forms.NotifyIcon { Icon = System.Drawing.SystemIcons.Application, ContextMenuStrip = menu, Text = "WaveTandem" };
        tray.DoubleClick += (_, _) => ShowFromTray();
        model.AppearanceChanged += ApplyAppearance;
        SystemEvents.UserPreferenceChanged += SystemPreferenceChanged;
        refresh.Tick += async (_, _) => await model.RefreshAsync();
        Loaded += async (_, _) =>
        {
            ApplyAppearance();
            await model.RefreshAsync();
            if (!shuttingDown) refresh.Start();
        };
        Closing += ConfirmClose;
    }

    private async void ConfirmClose(object? sender, CancelEventArgs e)
    {
        if (allowClose) return;
        e.Cancel = true;
        if (shuttingDown) return;
        if (!exiting && model.CloseToTray)
        {
            UpdateTray();
            tray.Visible = true;
            Hide();
            return;
        }
        shuttingDown = true;
        refresh.Stop();
        await model.CloseAsync();
        allowClose = true;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= SystemPreferenceChanged;
        model.AppearanceChanged -= ApplyAppearance;
        var menu = tray.ContextMenuStrip;
        tray.Dispose();
        menu?.Dispose();
        base.OnClosed(e);
    }

    private void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        tray.Visible = false;
    }

    private void UpdateTray()
    {
        openItem.Text = model.Text["Open"];
        toggleItem.Text = model.ToggleLabel;
        toggleItem.Enabled = model.CanToggle;
        exitItem.Text = model.Text["Exit"];
    }

    private void SystemPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (!Dispatcher.HasShutdownStarted) _ = Dispatcher.InvokeAsync(ApplyAppearance);
    }

    private void ApplyAppearance()
    {
        var app = Application.Current;
#pragma warning disable WPF0001 // .NET 10 Fluent theme switching.
        app.ThemeMode = model.Theme switch { "dark" => ThemeMode.Dark, "light" => ThemeMode.Light, _ => ThemeMode.System };
#pragma warning restore WPF0001
        var dark = model.Theme == "dark" || (model.Theme == "system" && SystemPrefersDark());
        var contrast = SystemParameters.HighContrast;
        SetBrush("Canvas", "#F5F7F6", "#18211F", SystemColors.WindowColor);
        SetBrush("Surface", "#FFFFFF", "#222E2B", SystemColors.WindowColor);
        SetBrush("Ink", "#17312B", "#EDF6F2", SystemColors.WindowTextColor);
        SetBrush("MutedInk", "#465C56", "#B8CBC3", SystemColors.WindowTextColor);
        SetBrush("Line", "#CFDAD6", "#4A6058", SystemColors.GrayTextColor);
        SetBrush("Accent", "#15674F", "#89D5B8", SystemColors.HighlightColor);
        SetBrush("InfoSurface", "#E7F3ED", "#2A4037", SystemColors.WindowColor);
        UpdateTray();

        void SetBrush(string key, string light, string darkValue, Color highContrast)
        {
            var brush = new SolidColorBrush(contrast ? highContrast : (Color)ColorConverter.ConvertFromString(dark ? darkValue : light));
            brush.Freeze();
            app.Resources[key] = brush;
        }
    }

    private static bool SystemPrefersDark() =>
        Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            "AppsUseLightTheme", 1) is 0;

    private async void ToggleSharing(object sender, RoutedEventArgs e) => await model.ToggleAsync();
    private async void RefreshDevices(object sender, RoutedEventArgs e) => await model.RefreshAsync();
    private void DismissWelcome(object sender, RoutedEventArgs e) => model.ShowWelcome = false;

    private void OpenSoundSettings(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:sound") { UseShellExecute = true })?.Dispose(); }
        catch (Win32Exception) { MessageBox.Show(this, model.Text["OpenSettingsError"], "WaveTandem"); }
    }
}
