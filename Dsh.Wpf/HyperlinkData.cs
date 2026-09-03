using System.Windows;

namespace Dsh.Wpf;

/// <summary>Attached flag so the Markdown renderer / control can distinguish file mentions from plain links.</summary>
public static class HyperlinkData
{
    public static readonly DependencyProperty IsFileProperty =
        DependencyProperty.RegisterAttached("IsFile", typeof(bool), typeof(HyperlinkData), new PropertyMetadata(false));

    public static void SetIsFile(DependencyObject o, bool v) => o.SetValue(IsFileProperty, v);
    public static bool GetIsFile(DependencyObject o) => (bool)o.GetValue(IsFileProperty);
}
