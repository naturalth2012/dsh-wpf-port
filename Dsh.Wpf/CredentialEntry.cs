using Dsh.App.Services;
using Dsh.Contract.Methods;
using Loc = Dsh.App.Services.Localization;

namespace Dsh.Wpf;

/// <summary>One credential row in the settings tab: ref name + status + writability.</summary>
public sealed record CredentialEntry(string Ref, bool Configured, string Source, bool Writable)
{
    public string StatusLabel => Configured ? Loc.Get("Cred.Configured") : Loc.Get("Cred.NotConfigured");

    /// <summary>E11: status dot color — Accent (green) when configured, Danger (red) when missing.</summary>
    public System.Windows.Media.Brush StatusBrush =>
        (Configured
            ? System.Windows.Application.Current.TryFindResource("Accent")
            : System.Windows.Application.Current.TryFindResource("Danger")) as System.Windows.Media.Brush
        ?? System.Windows.Media.Brushes.Gray;

    public static CredentialEntry From(string refName, CredentialView view) =>
        new(refName, view.Configured, view.Source ?? "", view.Writable);
}
