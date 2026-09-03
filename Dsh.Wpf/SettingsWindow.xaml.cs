using System.Windows;
using System.Windows.Media;

namespace Dsh.Wpf;

/// <summary>
/// Settings window (U5): a compact, non-modal dialog that reuses the main window's ViewModel
/// so theme/language/model changes apply instantly. Owner is assigned on open so it stays
/// above the main window but never blocks it.
/// </summary>
public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        // Guard: Surface1 must resolve to a theme token, otherwise the window renders
        // with the system default (light) background and breaks dark-mode consistency.
        if (TryFindResource("Surface1") is not SolidColorBrush)
            System.Diagnostics.Debug.WriteLine("[SettingsWindow] WARN: theme token 'Surface1' not resolved; window may render with default background.");
    }
}
