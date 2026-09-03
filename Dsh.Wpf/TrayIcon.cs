using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Forms;
using Loc = Dsh.App.Services.Localization;

namespace Dsh.Wpf;

/// <summary>
/// Notification-area (tray) integration: an icon with a context menu (J1). The window
/// minimizes to the tray on close; the tray's Show/Exit menu restores or quits. Uses
/// <see cref="NotifyIcon"/> from WinForms because WPF has no built-in tray control.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _notify;
    private readonly Func<Window> _getWindow;
    private readonly Action? _newSession;
    private readonly Action? _newWindow;

    public TrayIcon(Func<Window> getWindow, Action? newSession = null, Action? newWindow = null)
    {
        _getWindow = getWindow;
        _newSession = newSession;
        _newWindow = newWindow;
        var menu = new ContextMenuStrip();
        menu.Items.Add(Loc.Get("Tray.Show"), null, (_, _) => ShowWindow());
        menu.Items.Add(Loc.Get("NewSession"), null, (_, _) => _newSession?.Invoke());
        menu.Items.Add(Loc.Get("NewWindow"), null, (_, _) => _newWindow?.Invoke());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Loc.Get("Tray.Exit"), null, (_, _) => ((App)System.Windows.Application.Current).QuitFromShortcut());

        _notify = new NotifyIcon
        {
            Text = Loc.Get("App.Name"),
            Icon = LoadAppIcon(),
            Visible = true,
            ContextMenuStrip = menu,
        };
        _notify.DoubleClick += (_, _) => ShowWindow();
    }

    /// <summary>
    /// Show a transient tray balloon (P2-9), e.g. on connect/reconnect. No-op when the tray is
    /// not available (some environments suppress balloons).
    /// </summary>
    public void NotifyBalloon(string title, string text)
    {
        try
        {
            _notify.BalloonTipTitle = title;
            _notify.BalloonTipText = text;
            _notify.ShowBalloonTip(3000);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException
                                     or ObjectDisposedException)
        {
            // Balloons are best-effort (some environments suppress them).
            System.Diagnostics.Debug.WriteLine($"[TrayIcon] balloon failed: {ex.Message}");
        }
    }

    /// <summary>Load the app icon from the compiled WPF resource (Assets/app.ico).</summary>
    private static System.Drawing.Icon? LoadAppIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Assets/app.ico");
            using var stream = System.Windows.Application.GetResourceStream(uri)?.Stream;
            return stream is null ? SystemIcons.Application : new System.Drawing.Icon(stream);
        }
        catch (Exception ex) when (ex is IOException or UriFormatException or ArgumentException
                                     or NotSupportedException)
        {
            // Fall back to the system icon when the compiled resource cannot be read.
            System.Diagnostics.Debug.WriteLine($"[TrayIcon] icon load failed: {ex.Message}");
            return SystemIcons.Application;
        }
    }

    private void ShowWindow()
    {
        var win = _getWindow();
        win.Show();
        if (win.WindowState == WindowState.Minimized) win.WindowState = WindowState.Normal;
        win.Activate();
    }

    public void Dispose()
    {
        _notify.Visible = false;
        _notify.Dispose();
    }
}
