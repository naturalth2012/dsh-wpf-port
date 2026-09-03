using System.Windows;

namespace Dsh.Viewer;

/// <summary>
/// Local-storage viewer entry point (os/03, os/02 domain I). Reads <c>~/.dsh/sessions/</c>
/// off disk — no host, no network, no model. This is the L0 scaffold: the storage decode
/// (SessionPathResolver / ChunkRowExpander / SurfaceProjector) is not wired in yet.
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }
}
