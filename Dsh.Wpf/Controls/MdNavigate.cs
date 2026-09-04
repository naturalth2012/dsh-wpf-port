using System.Windows;
using System.Windows.Media;
using Application = System.Windows.Application;

namespace Dsh.Wpf.Controls;

/// <summary>
/// 自绘控件内 Markdown 链接 / H4 文件提及的打开逻辑：文件走 MainViewModel.OpenFileCommand，
/// 普通链接用系统浏览器打开（与既有 OpenWeb 一致）。
/// </summary>
public static class MdNavigate
{
    public static void Open(string? target, bool isFile)
    {
        if (string.IsNullOrWhiteSpace(target)) return;
        var vm = Application.Current.MainWindow?.DataContext as MainViewModel;
        if (isFile)
        {
            // Resolve relative paths (e.g. "today_date.txt" returned by the AI) against the
            // session cwd or harness root so that Process.Start and host.openPath both see
            // a real absolute path. Without this, the harness spawns PowerShell in its own
            // cwd and the file is never found.
            var sessionCwd = vm is null
                ? null
                : vm.Sessions.FirstOrDefault(s => s.Id == vm.SelectedSessionId)?.Cwd;
            target = FilePathResolver.Resolve(target, sessionCwd, vm?.HarnessDirectory);

            // HTML 文件：用系统默认浏览器打开（2026-09-02）。
            //
            // 这里原先是 File.ReadAllText + HtmlPreviewWindow 内嵌 WebView2 预览，
            // 但这是用户点击 HTML 链接的主要入口，而该路径长期显示空白：
            // WebView2 的 data: 源无法承载完整 HTML 页面（script/css/相对资源
            // 全部失败）。历经 file:///、虚拟主机映射等多种修复均未奏效，
            // 而系统浏览器打开同一文件始终正常（用户实测）。
            //
            // 与 BuildFileLink（裸文本 .html 链接）和围栏 HTML 块的"预览"
            // 按钮统一：一律走系统浏览器，行为一致且可靠。
            if (IsHtmlPath(target))
            {
                try
                {
                    if (OpenLocally(target)) return;
                }
                catch
                {
                    // 打开失败，回退到系统打开。
                }
                FallbackOpen(target);
                return;
            }
            // 建议4d: 其他文件（txt/office/pdf 等）——先尝试本地默认应用直接打开，
            // 不依赖 host.openPath；失败时回退到 VM 的 host.openPath。
            if (OpenLocally(target)) return;
            if (vm is not null)
            {
                vm.OpenFileCommand.Execute(target);
            }
            return;
        }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch
        {
            // 非法 URL 不应导致崩溃。
        }
    }

    private static bool IsHtmlPath(string path)
        => path.EndsWith(".html", System.StringComparison.OrdinalIgnoreCase)
           || path.EndsWith(".htm", System.StringComparison.OrdinalIgnoreCase);

    private static void FallbackOpen(string target)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[AMC] Navigate failed: {ex.Message}"); }
    }

    /// <summary>Open a local file with the OS default app. Returns true when the launch succeeded.</summary>
    private static bool OpenLocally(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }
}
