using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Dsh.Wpf;

/// <summary>
/// Win32 global hotkeys (P2-10): registers system-wide shortcuts (Ctrl+Alt+N → new session,
/// Ctrl+Alt+H → activate the primary window) via <c>RegisterHotKey</c>. Works even when the app
/// is minimized to the tray. Hotkeys are unregistered on dispose (window close).
/// </summary>
public sealed class GlobalHotKey : IDisposable
{
    // WM_HOTKEY = 0x0312.
    private const int WM_HOTKEY = 0x0312;
    private const int MOD_CONTROL = 0x0002;
    private const int MOD_ALT = 0x0001;

    private readonly IntPtr _hwnd;
    private readonly Dictionary<int, Action> _handlers = new();
    private int _nextId = 0xA000;

    public GlobalHotKey(Window window)
    {
        // Defensive (2026-08-30): HwndSource.FromHwnd throws ArgumentException ("零的 Hwnd 无效")
        // when handed IntPtr.Zero, which is exactly what happens if this is constructed before the
        // window's HWND exists (i.e. from the Window constructor). Callers must construct this from
        // SourceInitialized; we additionally fail soft here so a future mistake degrades to
        // "no hotkeys" instead of an unhandled UI-thread exception at startup.
        IntPtr handle = IntPtr.Zero;
        try
        {
            handle = new WindowInteropHelper(window).Handle;
        }
        catch
        {
            handle = IntPtr.Zero;
        }
        if (handle == IntPtr.Zero)
        {
            _hwnd = IntPtr.Zero;
            return;
        }

        var source = (HwndSource?)PresentationSource.FromVisual(window) ?? HwndSource.FromHwnd(handle);
        _hwnd = source?.Handle ?? IntPtr.Zero;
        if (source is not null)
        {
            source.AddHook(WndProc);
        }
    }

    /// <summary>True when the window had a usable HWND and hotkeys can actually be registered.</summary>
    public bool IsReady => _hwnd != IntPtr.Zero;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            if (_handlers.TryGetValue(id, out var action))
            {
                action();
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    /// <summary>Register Ctrl+Alt+<paramref name="key"/>. Returns the hotkey id, or -1 on failure.</summary>
    public int RegisterHotKey(System.Windows.Input.Key key, Action callback)
    {
        if (_hwnd == IntPtr.Zero) return -1;
        int id = _nextId++;
        int vk = System.Windows.Input.KeyInterop.VirtualKeyFromKey(key);
        if (RegisterHotKey(_hwnd, id, MOD_CONTROL | MOD_ALT, (uint)vk))
        {
            _handlers[id] = callback;
            return id;
        }
        return -1;
    }

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero)
        {
            foreach (var id in _handlers.Keys)
            {
                UnregisterHotKey(_hwnd, id);
            }
        }
        _handlers.Clear();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
