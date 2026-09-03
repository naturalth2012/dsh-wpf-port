using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Dsh.Wpf;

/// <summary>
/// Forces the OS title bar / non-client area into dark mode under Win10 1903+ and Win11.
/// Pure Win32 DWM call — no XAML change. Apply on window SourceInitialized and again whenever
/// the active theme flips between Light/Dark so the system chrome tracks the in-app theme.
/// </summary>
internal static class TitleBarDark
{
    // DWMWA_USE_IMMERSIVE_DARK_MODE = 20 (build 19041+). Older builds return failure and we
    // silently no-op; system chrome stays light, which is acceptable on legacy Windows.
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int pvAttribute, int cbAttribute);

    /// <summary>
    /// Applies the immersive dark-mode attribute to <paramref name="window"/>.
    /// If the HWND is not yet created (window not shown), defer until SourceInitialized.
    /// </summary>
    public static void Apply(Window window, bool dark)
    {
        if (window is null) return;
        var helper = new WindowInteropHelper(window);
        if (helper.Handle == IntPtr.Zero)
        {
            void OnSourceInit(object? _, EventArgs __)
            {
                window.SourceInitialized -= OnSourceInit;
                ApplyToHandle(new WindowInteropHelper(window).Handle, dark);
            }
            window.SourceInitialized -= OnSourceInit;
            window.SourceInitialized += OnSourceInit;
            return;
        }
        ApplyToHandle(helper.Handle, dark);
    }

    private static void ApplyToHandle(IntPtr hwnd, bool dark)
    {
        if (hwnd == IntPtr.Zero) return;
        var value = dark ? 1 : 0;
        // Result intentionally ignored: pre-1903 systems return failure and we no-op.
        _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
    }
}