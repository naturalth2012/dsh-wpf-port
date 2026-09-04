using System;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Wpf;
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Window = System.Windows.Window;

namespace Dsh.Wpf;

/// <summary>
/// 问题2：HTML 预览窗口。用 WebView2 渲染一段 HTML（来自 html 围栏代码块或网页工具输出）。
/// 独立窗口而非内嵌，避免 WebView2 的 HWND 顶层在虚拟化滚动列表中的穿透/裁剪问题。
/// </summary>
public sealed class HtmlPreviewWindow : Window
{
    private readonly WebView2 _webView = new() { DefaultBackgroundColor = System.Drawing.Color.Transparent };
    private readonly string _html;
    private readonly string? _markdown;     // original Markdown source (detail-window scenario only)
    private readonly bool _showToolbar;     // scenario switch — see the field comment below

    // ── Shared WebView2 environment (perf, 2026-09-01) ──────────────────────────────────────
    // CoreWebView2Environment.CreateAsync spins up the WebView2 runtime for the process. Doing it
    // on every window open meant every double-click paid that startup cost (hundreds of ms to a
    // second) — the "why does the window take so long to appear?" lag. The environment is process
    // wide and safe to share, so it is created once and reused.
    private static Task<Microsoft.Web.WebView2.Core.CoreWebView2Environment>? _sharedEnv;

    private static Task<Microsoft.Web.WebView2.Core.CoreWebView2Environment> GetSharedEnvironmentAsync()
        // All callers run on the UI thread; Lazy would be belt-and-braces here, but the null check
        // plus the fact that CreateAsync is idempotent-and-cheap-after-first-call makes this safe.
        => _sharedEnv ??= Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(null, null, null);

    // ── Scenario switch (2026-09-02) ───────────────────────────────────────────────────────
    // This window serves TWO different scenarios with different needs:
    //
    //  A) MESSAGE DETAIL (OpenMarkdown) — the user double-clicks a transcript message to read its
    //     full content. They want to TAKE it: copy the Markdown, copy as plain text, or save it.
    //     → toolbar ENABLED.
    //
    //  B) RAW HTML PREVIEW (Open) — rendering an HTML fragment (tool output, fenced html). The
    //     user just wants to LOOK at it; toolbar buttons are noise. Local .html files no longer
    //     even reach this window (they open in the system browser).
    //     → toolbar DISABLED, and the WebView2 is the content directly (no DockPanel wrapper),
    //       which is also the layout the early, working implementation used.
    //
    // An earlier revision applied a toolbar to BOTH scenarios; a later one removed it from both.
    // Both were wrong — the two scenarios genuinely differ.
    public HtmlPreviewWindow(string title, string html, string? markdown = null)
    {
        _html = html;
        _markdown = markdown;
        _showToolbar = !string.IsNullOrEmpty(markdown);
        Title = title;
        Width = 760;
        Height = 560;
        MinWidth = 480;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)Application.Current.TryFindResource("Surface1") ?? Brushes.White;

        if (_showToolbar)
        {
            // Toolbar on top, WebView2 filling the rest (last child fills in a DockPanel).
            var layout = new System.Windows.Controls.DockPanel();
            var toolbar = BuildToolbar();
            System.Windows.Controls.DockPanel.SetDock(toolbar, System.Windows.Controls.Dock.Top);
            layout.Children.Add(toolbar);
            layout.Children.Add(_webView);
            Content = layout;
        }
        else
        {
            // WebView2 IS the content — the early, working layout for plain HTML preview.
            Content = _webView;
        }

        Loaded += async (_, _) =>
        {
            try
            {
                // 在独立 window 中初始化 WebView2 并渲染 HTML（复用进程级共享环境）。
                // 无导航事件处理：HookNavigationHandlers 曾在此处拦截导航，
                // 误取消了承载内容的那次 NavigateToString → 窗口空白（回退到
                // 早期实现，详见 AssistantMessageControl 的注释）。
                var env = await GetSharedEnvironmentAsync();
                await _webView.EnsureCoreWebView2Async(env);
                _webView.CoreWebView2.NavigateToString(WrapHtml(_html));
            }
            catch (Exception ex)
            {
                var tb = new System.Windows.Controls.TextBlock
                {
                    Text = Dsh.App.Services.Localization.Format("Html.Unavailable", ex.Message),
                    Foreground = (Brush)System.Windows.Application.Current.TryFindResource("MdError") ?? System.Windows.Media.Brushes.Red,
                    Margin = new Thickness(12),
                };
                Content = tb;
            }
        };
    }

    // ── 导航事件处理（移除于 2026-09-02）──────────────────────────────────────────────────
    // 曾在此处注册 NavigationStarting 拦截，但 NavigateToString 触发的 NavigationStarting
    // 携带 WebView2 内部 URI，任何 scheme 拦截（白名单或黑名单）都可能 e.Cancel = true
    // 误取消承载内容的那次导航 → 窗口空白。因此不注册任何导航处理。

    /// <summary>
    /// Top toolbar for the message-detail scenario (Markdown source present): copy the original
    /// Markdown, copy as plain text, or save to a file. Only rendered when <see cref="_showToolbar"/>.
    /// </summary>
    private System.Windows.Controls.StackPanel BuildToolbar()
    {
        var bar = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            Margin = new Thickness(8, 6, 8, 6),
        };

        bar.Children.Add(MakeButton("Html.CopyMarkdown", "复制 Markdown", () => CopyToClipboard(_markdown!)));
        bar.Children.Add(MakeButton("Html.CopyText", "复制纯文本", () => CopyToClipboard(StripMarkdown(_markdown!))));
        bar.Children.Add(MakeButton("Html.SaveAs", "另存为", SaveAs));

        return bar;
    }

    private System.Windows.Controls.Button MakeButton(string locKey, string fallback, Action onClick)
    {
        var btn = new System.Windows.Controls.Button
        {
            Content = Dsh.App.Services.Localization.Get(locKey) ?? fallback,
            Padding = new Thickness(8, 3, 8, 3),
            Margin = new Thickness(0, 0, 6, 0),
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        btn.Click += (_, _) =>
        {
            try { onClick(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[HtmlPreviewWindow] toolbar failed: {ex.Message}"); }
        };
        return btn;
    }

    private static void CopyToClipboard(string text)
    {
        try
        {
            System.Windows.Clipboard.SetText(text);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HtmlPreviewWindow] clipboard failed: {ex.Message}");
        }
    }

    private void SaveAs()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = SanitizeFileName(Title),
            DefaultExt = ".md",
            Filter = "Markdown (*.md)|*.md|Text (*.txt)|*.txt|HTML (*.html)|*.html",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            System.IO.File.WriteAllText(dlg.FileName, _markdown!);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HtmlPreviewWindow] save failed: {ex.Message}");
        }
    }

    private static string SanitizeFileName(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "detail";
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var cleaned = new string(title.Select(c => invalid.Contains(c) ? '-' : c).ToArray());
        return cleaned.Length > 60 ? cleaned[..60] : cleaned;
    }

    /// <summary>Rough Markdown → plain text for the "copy as text" button.</summary>
    private static string StripMarkdown(string md)
    {
        if (string.IsNullOrEmpty(md)) return "";
        var sb = new System.Text.StringBuilder(md.Length);
        bool inFence = false;
        foreach (var rawLine in md.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine;
            if (line.TrimStart().StartsWith("```")) { inFence = !inFence; continue; }
            if (inFence) { sb.AppendLine(line); continue; }
            line = System.Text.RegularExpressions.Regex.Replace(line, @"^\s{0,3}#{1,6}\s*", "");
            line = System.Text.RegularExpressions.Regex.Replace(line, @"^\s{0,3}>\s?", "");
            line = System.Text.RegularExpressions.Regex.Replace(line, @"^\s*([-*+]|\d+\.)\s+", "");
            line = System.Text.RegularExpressions.Regex.Replace(line, @"\*\*(.+?)\*\*", "$1");
            line = System.Text.RegularExpressions.Regex.Replace(line, @"(?<!\w)\*(.+?)\*(?!\w)", "$1");
            line = System.Text.RegularExpressions.Regex.Replace(line, @"`([^`]+)`", "$1");
            line = System.Text.RegularExpressions.Regex.Replace(line, @"\[([^\]]+)\]\([^)]*\)", "$1");
            sb.AppendLine(line);
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>把裸 HTML 片段包成完整文档，避免缺少 &lt;html&gt;/&lt;body&gt; 时样式不生效。CSS 主题感知（H 重构 2026-08-21）。</summary>
    private static string WrapHtml(string html)
    {
        bool dark = (ThemeSettings.Load().Theme ?? "Light").Trim().ToLowerInvariant() == "dark";
        string bodyColor = dark ? "#E2E8F0" : "#1e293b";
        string codeBg = dark ? "#0B0F0F" : "#f1f5f9";
        string codeFg = dark ? "#E2E8F0" : "#0f172a";
        string link = dark ? "#14B8A6" : "#0F766E";
        // Table styling (2026-09-01): Markdig's GFM tables now reach this window, and unstyled
        // <table> elements render as a borderless wall of text. Borders + zebra rows keep the
        // popup legible and match the inline view's table look.
        string borderColor = dark ? "#334155" : "#cbd5e1";
        string rowAlt = dark ? "#0d1212" : "#f8fafc";
        string headBg = dark ? "#12201f" : "#e2e8f0";
        return $"<!DOCTYPE html><html><head><meta charset='utf-8'><style>" +
               $"body{{font-family:system-ui,Segoe UI;margin:16px;color:{bodyColor};line-height:1.6;background:transparent}}" +
               $"img{{max-width:100%}}" +
               $"pre{{background:{codeBg};color:{codeFg};padding:8px;border-radius:6px;overflow:auto}}" +
               $"code{{background:{codeBg};color:{codeFg};padding:1px 4px;border-radius:3px}}" +
               $"a{{color:{link}}}" +
               $"table{{border-collapse:collapse;margin:8px 0;max-width:100%}}" +
               $"th,td{{border:1px solid {borderColor};padding:4px 10px;text-align:left;vertical-align:top}}" +
               $"th{{background:{headBg}}}" +
               $"tr:nth-child(even){{background:{rowAlt}}}" +
               $"</style></head><body>{html}</body></html>";
    }

    /// <summary>便捷打开：若调用线程是 UI 线程则直接 ShowDialog，否则切换到 UI 线程。</summary>
    public static void Open(string title, string html, Window? owner = null)
    {
        var window = new HtmlPreviewWindow(title, html) { Owner = owner ?? System.Windows.Application.Current.MainWindow };
        Application.Current.Dispatcher.Marshal(() => window.ShowDialog());
    }

    /// <summary>
    /// Markdown preview (建议4c): renders via <see cref="MarkdownRenderer.ToHtml"/> — the SAME
    /// Markdig pipeline (GFM pipe tables included) the inline transcript uses.
    /// <para>
    /// 2026-09-01: this used to run a hand-rolled Markdown-subset converter with NO table
    /// support, so GFM pipe tables showed as literal "| a | b |" text and the popup looked
    /// nothing like the inline view. Two renderers for the same content was the root cause;
    /// there is now exactly one.
    /// </para>
    /// </summary>
    public static void OpenMarkdown(string title, string markdown, Window? owner = null)
    {
        // Message-detail scenario: pass the Markdown source so the toolbar (copy/save) is shown.
        var window = new HtmlPreviewWindow(title, Dsh.App.MarkdownRenderer.ToHtml(markdown), markdown)
        {
            Owner = owner ?? Application.Current.MainWindow,
        };
        Application.Current.Dispatcher.Marshal(() => window.ShowDialog());
    }
}
