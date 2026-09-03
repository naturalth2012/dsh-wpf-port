using System.Runtime.InteropServices;
using System.Windows;
using Dsh.App.Services;
using Microsoft.Extensions.Logging;
using Loc = Dsh.App.Services.Localization;

namespace Dsh.Wpf;

public partial class App : System.Windows.Application
{
    private TrayIcon? _tray;
    private ILogger? _log;

    /// <summary>Tray instance for balloon notifications (P2-9); null until startup.</summary>
    public static TrayIcon? Tray { get; private set; }

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    // PROCESS_PER_MONITOR_DPI_AWARE = -4; per-monitor DPI (J6) so the window scales
    // correctly on mixed-DPI displays. Set before any window is created.
    private static readonly IntPtr DpiContextPerMonitorV2 = new(-4);

    public App()
    {
        try
        {
            SetProcessDpiAwarenessContext(DpiContextPerMonitorV2);
        }
        catch
        {
            // DPI awareness is best-effort; a failure degrades to system DPI scaling.
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // P2-16: restore the persisted theme before any window is shown.
        ApplyTheme(ThemeSettings.Load().Theme);
        // P2-8: restore the persisted language.
        Dsh.App.Services.Localization.SetLanguage(AppSettings.Load().Language);
        _log = Logging.Get<App>();
        // Convert any unhandled UI-thread exception into a logged diagnostic instead of a
        // silent crash-and-exit, and keep the window alive when feasible.
        DispatcherUnhandledException += (_, args) =>
        {
            _log?.LogError(args.Exception, "Unhandled UI-thread exception");
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) _log?.LogCritical(ex, "Unhandled process exception");
        };
        _tray = new TrayIcon(GetPrimaryWindow,
            newSession: () => (GetPrimaryWindow()?.DataContext as MainViewModel)?.NewSessionCommand.Execute(null),
            newWindow: () => new MainWindow().Show());
        Tray = _tray;
        // P2-9: surface connect/reconnect as a tray balloon.
        ConnectionScope.Instance.ConnectionStateChanged += up =>
        {
            if (up) _tray?.NotifyBalloon(Loc.Get("Tray.ConnectedTitle"), Loc.Get("Tray.Reconnected"));
        };
        // Single window; StartupUri is unset so exactly one window is created here.
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    /// <summary>The first still-open window, or a fresh one.</summary>
    private Window GetPrimaryWindow()
        => Windows.Count > 0 ? Windows[0] : new MainWindow();

    /// <summary>
    /// Apply a named theme by swapping the merged ResourceDictionary (P2-16).
    /// Unknown names fall back to Light. Safe to call before windows exist.
    /// </summary>
    public static void ApplyTheme(string theme)
    {
        System.Diagnostics.Debug.WriteLine($"[ApplyTheme] theme='{theme}'");
        var uri = theme.Trim().ToLowerInvariant() switch
        {
            "dark" => new Uri("pack://application:,,,/Themes/DarkTheme.xaml", UriKind.Absolute),
            _ => new Uri("pack://application:,,,/Themes/LightTheme.xaml", UriKind.Absolute),
        };
        var dict = new ResourceDictionary { Source = uri };
        // Replace any previously-merged theme dictionary (tagged with a known key).
        const string Tag = "AppBackgroundBrush";
        var existing = Current.Resources.MergedDictionaries
            .FirstOrDefault(d => d.Contains(Tag));
        if (existing is not null)
            Current.Resources.MergedDictionaries.Remove(existing);
        Current.Resources.MergedDictionaries.Add(dict);
        // Sync the OS title bar with the active theme (Win10 1903+ / Win11).
        bool dark = dict.Source.ToString().Contains("DarkTheme", StringComparison.OrdinalIgnoreCase);
        foreach (Window win in Current.Windows)
            TitleBarDark.Apply(win, dark);
    }

    /// <summary>
    /// Genuine quit (tray Exit / Ctrl+Q): flags every window to allow close, then shuts
    /// down so OnClosing doesn't hide each window to the tray instead.
    /// </summary>
    public void QuitFromShortcut()
    {
        foreach (var win in Windows)
        {
            if (win is MainWindow main) main.AllowClose();
        }
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        // P1-fix: dispose every VM's scope subscriptions before Detach so the singleton stops
        // invoking handlers the moment the shutdown begins (covers the case where Shutdown
        // bypassed individual MainWindow.OnClosing calls — e.g. forced termination).
        foreach (var win in Windows)
        {
            if (win is MainWindow mw && mw.DataContext is MainViewModel mainVm) mainVm.Dispose();
        }
        // P1-14: stop the shared downstream streams (safe even if a window already detached).
        ConnectionScope.Instance.Detach();
        // Stop a backend process this app launched (harness auto-start).
        if (MainWindow?.DataContext is MainViewModel vm)
        {
            try { vm.StopHarness(); }
            catch { /* best-effort */ }
        }
        base.OnExit(e);
    }
}
