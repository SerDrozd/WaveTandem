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
    private readonly Forms.ToolStripMenuItem openItem = new(), toggleItem = new(), pauseItem = new(), exitItem = new();
    private bool allowClose, exiting, shuttingDown;

    public MainWindow(MainViewModel model)
    {
        this.model = model;
        InitializeComponent();
        DataContext = model;
        var menu = new Forms.ContextMenuStrip();
        menu.Items.AddRange([openItem, toggleItem, pauseItem, exitItem]);
        openItem.Click += (_, _) => ShowFromTray();
        toggleItem.Click += async (_, _) => await model.ToggleAsync();
        pauseItem.Click += async (_, _) => await model.TogglePauseAsync();
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
        PreviewKeyDown += StartShortcut;
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
        pauseItem.Text = model.PauseLabel;
        pauseItem.Enabled = model.CanPause;
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
        app.ThemeMode = model.Theme == "dark" ? ThemeMode.Dark : ThemeMode.Light;
#pragma warning restore WPF0001
        var dark = model.Theme == "dark";
        var contrast = SystemParameters.HighContrast;
        SetBrush("Canvas", "#F6F7F9", "#181B21", SystemColors.WindowColor);
        SetBrush("Surface", "#FFFFFF", "#22262E", SystemColors.WindowColor);
        SetBrush("Ink", "#1A202A", "#EFF2F8", SystemColors.WindowTextColor);
        SetBrush("MutedInk", "#4B5668", "#BAC2D0", SystemColors.WindowTextColor);
        SetBrush("Line", "#D4DAE3", "#444C5B", SystemColors.GrayTextColor);
        SetBrush("Accent", "#2D548F", "#ADC9FF", SystemColors.WindowTextColor);
        UpdateTray();

        void SetBrush(string key, string light, string darkValue, Color highContrast)
        {
            var brush = new SolidColorBrush(contrast ? highContrast : (Color)ColorConverter.ConvertFromString(dark ? darkValue : light));
            brush.Freeze();
            app.Resources[key] = brush;
        }
    }

    private async void ToggleSharing(object sender, RoutedEventArgs e) => await model.ToggleAsync();
    private async void PauseCopies(object sender, RoutedEventArgs e) => await model.TogglePauseAsync();
    private async void RefreshDevices(object sender, RoutedEventArgs e) => await model.RefreshAsync();
    private void DismissWelcome(object sender, RoutedEventArgs e) => model.ShowWelcome = false;
    private void SaveSetup(object sender, RoutedEventArgs e) => model.SaveSetup();
    private void LoadSetup(object sender, RoutedEventArgs e) => model.LoadSetup();
    private void DeleteSetup(object sender, RoutedEventArgs e) => model.DeleteSetup();

    private async void TestListener(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DeviceRow row }) await model.TestAsync(row);
    }

    private async void StartShortcut(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.Control) return;
        e.Handled = true;
        if (!e.IsRepeat) await model.ToggleAsync();
    }

    private void CopyDiagnostics(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(model.GetSupportReport());
            ClipboardStatus.Text = model.Text["DiagnosticsCopied"];
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            ClipboardStatus.Text = model.Text["ClipboardUnavailable"];
        }
    }

    private void OpenSoundSettings(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:sound") { UseShellExecute = true })?.Dispose(); }
        catch (Win32Exception) { MessageBox.Show(this, model.Text["OpenSettingsError"], "WaveTandem"); }
    }
}
