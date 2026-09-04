using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Dsh.App;
using Loc = Dsh.App.Services.Localization;
// 本工程同时启用 WPF + WinForms，导致大量简单类型名歧义；以下别名统一定向 WPF。
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using Cursors = System.Windows.Input.Cursors;
using FontFamily = System.Windows.Media.FontFamily;
using FontStyle = System.Windows.FontStyle;
using FontWeight = System.Windows.FontWeight;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using UniformGrid = System.Windows.Controls.Primitives.UniformGrid;
using UserControl = System.Windows.Controls.UserControl;

namespace Dsh.Wpf.Controls;

/// <summary>
/// M1/M4/自绘重构：把一条助手消息的 Markdown 用代码直接构建视觉树（替代脆弱的
/// DataTemplate + 绑定 + 选择器拼装）。<see cref="Text"/>/<see cref="Reasoning"/> 变化时
/// 触发 <see cref="Rebuild"/>，只重建这一个控件。所有颜色从主题资源（Md* 令牌）取用，
/// 深浅主题自动切换，无硬编码色。
/// </summary>
public sealed partial class AssistantMessageControl : UserControl
{
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(nameof(Text), typeof(string), typeof(AssistantMessageControl),
            new PropertyMetadata("", (d, _) => ((AssistantMessageControl)d).Rebuild()));

    public static readonly DependencyProperty ReasoningProperty =
        DependencyProperty.Register(nameof(Reasoning), typeof(string), typeof(AssistantMessageControl),
            new PropertyMetadata(null, (d, _) => ((AssistantMessageControl)d).Rebuild()));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string? Reasoning
    {
        get => (string?)GetValue(ReasoningProperty);
        set => SetValue(ReasoningProperty, value);
    }

    /// <summary>内容根容器（代码构建，无 XAML）。</summary>
    private readonly StackPanel _root = new() { Margin = new Thickness(0, 1, 0, 1) };

    /// <summary>单条消息默认渲染的最大块数（防止单条超长消息一次性构建数百个块导致卡顿）。</summary>
    private const int DefaultMaxBlocks = 200;

    /// <summary>单条消息文本超过此长度时跳过 Markdown，直接纯文本渲染（防 UI 线程卡死）。</summary>
    private const int MaxRenderBytes = 256 * 1024;

    private IReadOnlyList<MarkdownBlock> _blocks = [];
    private int _maxBlocks = DefaultMaxBlocks;

    /// <summary>上次已渲染的完整文本（增量渲染前缀判断基准）。</summary>
    private string _renderedText = "";

    /// <summary>当前是否处于"纯文本增量追加"模式（流式 O(n²) 消除）。</summary>
    private bool _incrementalTail;

    /// <summary>流式累积超过此长度即切换到纯文本增量追加，避免反复全量 Markdig 重解析（O(n²)）。
    /// 4KB（P0-2，由 16KB 下调）：流式时每次 flush 都会对整条累积文本全量重解析，16KB 阈值下
    /// 一条 15KB 的消息要被反复全量解析数十次。4KB 在"排版质量"与"UI 响应"之间取更早的投降点：
    /// 短消息（绝大多数）仍走完整 Markdown，长流式输出尽早退化为纯文本增量追加。</summary>
    private const int IncrementalThreshold = 4 * 1024;

    public AssistantMessageControl()
    {
        // 纯代码控件：根容器直接作为内容。背景透明，由外层气泡承载。
        Content = _root;
        Background = Brushes.Transparent;

        // 容器回收复用（VirtualizationMode=Recycling）时 WPF 复用同一个控件实例去显示
        // 另一条消息。_renderedText / _incrementalTail 是实例状态，若不重置就会串台：
        // 上一条消息残留 _incrementalTail=true 且 _renderedText 为空串时，新消息的
        // text.StartsWith("") 恒为 true → 走增量追加分支（该分支不 Clear 直接 Add）
        // → 旧内容残留 + 新内容叠加，滚动到高密度区域时表现为整屏错乱/空白。
        // DataContextChanged 是虚拟化复用的可靠通知点（2026-08-31 修复）。
        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>
    /// 容器被复用显示另一条消息时重置渲染状态，强制下次 Rebuild 走完整重建路径。
    /// 不重置会让增量追加分支误判为"同一条消息在流式追加"，导致内容叠加。
    /// </summary>
    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        _renderedText = "";
        _incrementalTail = false;
        _blocks = [];
        _maxBlocks = DefaultMaxBlocks;
    }

    private void Rebuild()
    {
        // 模板应用/虚拟化 early-realize 阶段 TextProperty 回调可能早于本地值绑定触发，
        // 此时 Text 可为 null；统一兜底为空串，避免 line 91/line 115 的 text.Length/StartsWith 抛 NRE
        //（大会话高并发 realize 时稳定复现，导致整个 TranscriptList 视觉树构建被打断→空白/假死）。
        string text = Text ?? string.Empty;

        // 增量追加分支的前置守卫（2026-08-31）：只有当"确实已渲染过本条消息的前缀"时才允许
        // 走不 Clear 的追加路径。_renderedText 为空说明这是首次渲染或容器刚被复用重置，
        // 必须走下方完整重建；否则会出现"旧内容未清除又追加新内容"的叠加。
        bool canAppend = _incrementalTail
                         && _renderedText.Length > 0
                         && _root.Children.Count > 0;

        // 整段渲染加兜底（2026-08-31）：虚拟化容器复用 + 大文本渲染下，任一步抛异常都会让
        // 控件停在"已 Clear 但未填充"的半清空状态 → 用户看到整屏空白。这里捕获后降级为
        // 纯文本，保证容器永远有内容，异常仅记录不影响其他行。
        try
        {
            RebuildCore(text, canAppend);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AssistantMessageControl] RebuildCore failed, falling back: {ex.Message}");
            if (TryFallbackToPlainText(text, canAppend)) return;
            // 降级失败：保持现状，至少不扩散异常到视觉树构建。
        }
    }

    private bool TryFallbackToPlainText(string text, bool canAppend)
    {
        try
        {
            _root.Children.Clear();
            _root.Children.Add(BuildPlainLine(text));
            _renderedText = text;
            _incrementalTail = false;
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AssistantMessageControl] Fallback also failed: {ex.Message}");
            return false;
        }
    }

    private void RebuildCore(string text, bool canAppend)
    {

        // 流式 O(n²) 消除（2026-08-24 用户"大数据量会话卡死"主因）：SyncFoldToUi 每 250ms 用
        // 完整累积文本设置 Text，若每次都全量 Markdig.Parse（长度 0→100KB 递增），累积是 O(n²)。
        // 这里检测"纯追加流式"（新文本以已渲染文本为前缀）：
        //   - 已处于增量模式 → 只把新增尾部追加一个纯文本 TextBlock，O(尾部长度) 一次，无重解析。
        //   - 累积文本超过阈值（且非 console/超长）→ 切换到增量模式，完整渲染一次后开始增量追加。
        // 非前缀（历史重放/整条重设/回退）→ 走下方完整 Rebuild 路径。
        if (canAppend && text.StartsWith(_renderedText, StringComparison.Ordinal))
        {
            string tail = text[_renderedText.Length..];
            if (tail.Length > 0)
            {
                _root.Children.Add(BuildPlainLine(tail));
                _renderedText = text;
            }
            return;
        }

        _root.Children.Clear();
        _incrementalTail = false;

        if (!string.IsNullOrWhiteSpace(Reasoning))
        {
            _root.Children.Add(BuildReasoningExpander(Reasoning));
        }

        // 超长保护（必须在 console 分支之前）：极端大 tool output（如 glob 命中成千上万条
        // 路径、超长 read 结果）走 BuildConsoleOutput 同样是对几 MB 字符串做 Split('\n') +
        // 视觉树构建，UI 线程一样卡（2026-08-24 用户复现仍卡，且 console 分支先命中导致
        // "查看完整内容"门不出现）。所以超阈值一律先被门拦截（纯文本 O(n) 完整渲染放到
        // 用户点击时）；正常长度才继续判定 console / Markdown。
        if (text.Length > MaxRenderBytes)
        {
            _root.Children.Add(BuildLargeContentGate(text));
            _renderedText = text;
            return;
        }

        // 工具输出（ls / pwsh / dir 等）往往是多行对齐纯文本，其中连续的 "-" 会被 Markdig
        // 误判为 GFM 表格分隔行，导致 BuildTable 用等宽列把中文挤成 □（问题B）。
        // 命中"类控制台输出"模式时直接走纯文本渲染（Consolas），跳过 Markdown 解析；
        // 纯文本分支内仍对裸文件路径做扫描，命中本地文件则渲染为可点击的预览入口（问题C）。
        if (LooksLikeConsoleOutput(text))
        {
            _root.Children.Add(BuildConsoleOutput(text));
            _renderedText = text;
            return;
        }

        // 纯追加流式且累积已超阈值：切到增量模式（完整渲染当前一次，之后只追加尾部）。
        // 超长流式消息对 Markdown 排版需求低，纯文本增量保 UI 响应优先。
        // E9：阈值降到 16KB 且"主动投降"——只要流式累积超过阈值就进增量，不再尝试一次
        // 完整 Markdig parse（50-500KB 区间单次 parse 100-300ms，会卡 UI）。
        if (text.Length >= IncrementalThreshold && text.StartsWith(_renderedText, StringComparison.Ordinal))
        {
            _renderedText = text;
            _incrementalTail = true;
            _root.Children.Add(BuildPlainLine(text));
            return;
        }

        _renderedText = text;
        _blocks = MarkdownRenderer.Render(text);
        _maxBlocks = DefaultMaxBlocks;
        RenderBlocks();
    }

    /// <summary>构建一行纯文本（增量追加 / 增量模式首行）。</summary>
    private TextBlock BuildPlainLine(string text)
    {
        var tb = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        tb.SetResourceReference(ForegroundProperty, "MdTextPrimary");
        return tb;
    }

    /// <summary>
    /// 判断一段文本是否像终端/控制台输出（ls -l / pwsh dir / 多列对齐纯文本）。
    /// 这些文本不是 Markdown，但含有连续 "-" 等字符，会被 GFM 表格解析误伤。
    /// </summary>
    private static bool LooksLikeConsoleOutput(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        // P0-2: 短路——只扫描前 MaxConsoleProbeLines 行。原实现对全部分割行跑 3 个正则，
        // 上千行的工具输出意味着数千次正则匹配（每次 Rebuild 都重来）。控制台特征（权限串、
        // PS 表头、dir 时间戳）一律出现在输出开头，扫前 20 行即可判定，行为等价但 O(1)。
        const int MaxConsoleProbeLines = 20;
        var lines = text.Split('\n');
        if (lines.Length < 2) return false;

        // 命中常见控制台模式：Unix 权限串（-rw-r--r-- / drwxr-xr-x）、PowerShell 表头
        // （Mode Length LastWriteTime Name）、Windows dir 日期时间、多列对齐。
        int hits = 0;
        int probe = Math.Min(lines.Length, MaxConsoleProbeLines);
        for (int i = 0; i < probe; i++)
        {
            var line = lines[i];
            if (line.Length == 0) continue;
            if (UnixMode().IsMatch(line)) hits++;
            else if (PsHeader().IsMatch(line)) hits++;
            else if (WinDirStamp().IsMatch(line)) hits++;
        }
        if (hits >= 2) return true;

        // 退路：超过 4 行且出现"空格对齐的列 + 连续短横线分隔"，也当作控制台输出。
        // 只在探测窗口内查找，避免对超大文本做整串 Contains 扫描。
        if (lines.Length >= 4)
        {
            var probeSpan = string.Join('\n', lines, 0, probe);
            if (probeSpan.Contains("----")) return true;
        }
        return false;
    }

    private static readonly System.Text.RegularExpressions.Regex UnixModeCache =
        new(@"^\s*[-dclpsbD][-rwxsStTLL]{9}", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static System.Text.RegularExpressions.Regex UnixMode() => UnixModeCache;

    private static readonly System.Text.RegularExpressions.Regex PsHeaderCache =
        new(@"^\s*Mode\s+-----*\s+Length\s+------*\s+LastWriteTime", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static System.Text.RegularExpressions.Regex PsHeader() => PsHeaderCache;

    private static readonly System.Text.RegularExpressions.Regex WinDirStampCache =
        new(@"\d{1,2}/\d{1,2}/\d{2,4}\s+\d{1,2}:\d{2}\s+[AP]M\s+\d+", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static System.Text.RegularExpressions.Regex WinDirStamp() => WinDirStampCache;

    /// <summary>
    /// 超长内容的"查看完整"门：只渲染一行提示 + 一个按钮，避免自动渲染超大文本卡死 UI。
    /// 点击按钮后按纯文本（O(n)）完整渲染 <paramref name="fullText"/> —— 不喂 Markdig，
    /// 保留全部数据供用户查看（E6 信息完整性设计）。
    /// </summary>
    private UIElement BuildLargeContentGate(string fullText)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 2, 0, 2) };
        panel.Children.Add(MakeTextBlock(MdSpec.Small, Theme("MdTextMuted"), mono: false,
            text: Loc.Format("Ui.LargeContentTruncated", fullText.Length)));

        var btn = new Button
        {
            Content = Loc.Get("Ui.ViewFullContent"),
            FontSize = MdSpec.Small,
            Cursor = Cursors.Hand,
            Padding = new Thickness(6, 2, 6, 2),
            Margin = new Thickness(0, 4, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        btn.Click += (_, _) =>
        {
            try
            {
                var full = BuildConsoleOutput(fullText);
                _root.Children.Remove(panel);
                _root.Children.Add(full);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[AMC] RebuildCore failed: {ex.Message}"); }
        };
        panel.Children.Add(btn);
        return panel;
    }

    /// <summary>
    /// 纯文本渲染控制台输出：Consolas 字体、保留换行，对裸文件路径（本地存在）渲染为
    /// 可点击的预览入口（问题C：HTML / MD 文件可被预览）。
    /// </summary>
    /// <summary>P1-4: 控制台输出一次渲染的最大行数。工具输出（glob 命中上千路径、超长 read）
    /// 会为每一行创建一个 TextBlock 进视觉树，上千行 = 上千个控件的 Measure/Arrange，直接
    /// 卡死 UI 线程。超出部分先折叠，点"展开全部"再补齐。</summary>
    private const int MaxConsoleLines = 200;

    private UIElement BuildConsoleOutput(string text)
    {
        // DIAG (red-boxed session freeze): time the per-render scan and count how many file
        // names we matched/kept. A red-boxed session is suspected to have many console-output
        // lines that greedily match FileNamePattern (paths like /c/users/.../dsh-wpf-port
        // dangle into the regex, then we build a WrapPanel of TextBlocks for each). The
        // numbers below tell us whether the freeze is here or in the caller.
        var swBuild = System.Diagnostics.Stopwatch.StartNew();
        var allLines = text.Replace("\r\n", "\n").Split('\n');
        int totalLines = allLines.Length;
        bool truncated = allLines.Length > MaxConsoleLines;
        var lines = truncated ? allLines[..MaxConsoleLines] : allLines;
        int fileLinkCount = 0;
        int fileNameMatches = 0;

        var panel = new StackPanel { Margin = new Thickness(0, 2, 0, 2) };

        // Hoist the theme-brush lookups out of the loop: Theme() does a TryFindResource tree walk,
        // and doing it once per line meant 200+ walks per render for the same two brushes.
        var brushPrimary = Theme("MdTextPrimary");
        var brushMuted = Theme("MdTextMuted");

        long regexTicks = 0, accessTicks = 0;
        int accessibleCalls = 0;
        // P0 watchdog: a hard wall-clock budget for the whole scan. Even if some future input
        // defeats the regex/length caps above, the UI can NEVER freeze — we degrade to plain text
        // for the remainder and log it, instead of stalling the message pump.
        const long WatchdogMs = 400;
        bool watchdogFired = false;
        for (int lineNo = 0; lineNo < lines.Length; lineNo++)
        {
            var raw = lines[lineNo];
            // NOTE: per-line logging was REMOVED deliberately (2026-08-30). FileLogger writes
            // synchronously (File.Exists + FileInfo.Length + File.AppendAllText) under a lock on
            // the UI thread. Logging every 10 lines meant ~20 blocking file appends PER RENDER —
            // with virtualization realizing many items while scrolling, that alone froze the
            // window. Only the summary (and the watchdog) log, once per render.
            string line = raw;

            // P0 watchdog: once we blow the budget, stop ALL expensive work (regex + disk probe +
            // link building) and dump the remaining lines as plain text. Guarantees the message
            // pump keeps running no matter what the input looks like.
            if (!watchdogFired && swBuild.ElapsedMilliseconds > WatchdogMs)
            {
                watchdogFired = true;
                Dsh.Wpf.Logging.Log.Warn(
                    $"[DIAG] WATCHDOG fired at line i={lineNo}/{lines.Length} len={raw.Length} regexMs={regexTicks / 10000} accessMs={accessTicks / 10000} — remaining lines rendered as plain text");
            }
            if (watchdogFired)
            {
                panel.Children.Add(MakeTextBlock(MdSpec.Small, brushPrimary, mono: true, text: line));
                continue;
            }

            // P0: skip file-link detection entirely for absurdly long lines (bounded work).
            if (line.Length > MaxLinkScanChars)
            {
                panel.Children.Add(MakeTextBlock(MdSpec.Small, brushPrimary, mono: true, text: line));
                continue;
            }
            // 尝试从本行抽取一个文件路径（裸文本，无反引号）。
            var swRegex = System.Diagnostics.Stopwatch.StartNew();
            var match = FileNamePattern().Match(line);
            regexTicks += swRegex.ElapsedTicks;
            if (match.Success)
            {
                fileNameMatches++;
                string fileName = match.Value;
                // 仅当文件名看起来像本地可访问文件（含扩展名且非纯目录名）才做成可点击入口。
                if (fileName.IndexOf('.') > 0 && fileLinkCount < MaxFileLinksPerMessage)
                {
                    var swAccess = System.Diagnostics.Stopwatch.StartNew();
                    bool accessible = IsAccessibleFile(fileName);
                    accessTicks += swAccess.ElapsedTicks;
                    accessibleCalls++;
                    // Only log probes that actually reached the disk AND were slow. The syntactic
                    // gate above now rejects the pathological paths before any I/O, so this should
                    // normally never fire; if it does, it names the exact offending path.
                    if (swAccess.ElapsedMilliseconds >= 20)
                    {
                        Dsh.Wpf.Logging.Log.Warn(
                            $"[DIAG] slow disk probe i={lineNo} fileName='{fileName}' len={fileName.Length} result={accessible} ms={swAccess.ElapsedMilliseconds}");
                    }
                    if (!accessible)
                    {
                        panel.Children.Add(MakeTextBlock(MdSpec.Small, brushPrimary, mono: true, text: line));
                        continue;
                    }
                    fileLinkCount++;
                    var row = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 1) };
                    // 文件名之前的部分作为普通文本
                    int idx = line.IndexOf(fileName, StringComparison.Ordinal);
                    if (idx > 0)
                    {
                        row.Children.Add(MakeTextBlock(MdSpec.Small, brushPrimary, mono: true,
                            text: line.Substring(0, idx)));
                    }
                    row.Children.Add(BuildFileLink(fileName));
                    // 文件名之后的部分
                    int after = idx + fileName.Length;
                    if (after < line.Length)
                    {
                        row.Children.Add(MakeTextBlock(MdSpec.Small, brushPrimary, mono: true,
                            text: line.Substring(after)));
                    }
                    panel.Children.Add(row);
                    continue;
                }
            }
            panel.Children.Add(MakeTextBlock(MdSpec.Small, brushPrimary, mono: true, text: line));
        }

        // DIAG: log per-render cost + the numbers that drive it — but ONLY when it is slow or the
        // watchdog fired. FileLogger writes synchronously on the UI thread, so an unconditional
        // log here would add a blocking file append to every realized item while scrolling.
        // Healthy renders (the common case) must stay completely silent.
        if (watchdogFired || swBuild.ElapsedMilliseconds >= 20)
        {
            Dsh.Wpf.Logging.Log.Warn(
                $"[DIAG] BuildConsoleOutput SLOW lines={totalLines} rendered={lines.Length} fileNameMatches={fileNameMatches} fileLinks={fileLinkCount} accessibleCalls={accessibleCalls} textLen={text.Length} elapsedMs={swBuild.ElapsedMilliseconds} regexMs={regexTicks / 10000} accessMs={accessTicks / 10000} watchdog={watchdogFired}");
        }

        // P1-4: 折叠提示 + "展开全部"。展开时把剩余行一次性补齐（用户主动触发，可接受一次成本），
        // 而不是在每次自动 Rebuild 时都构建上千个 TextBlock。
        if (truncated)
        {
            int remaining = allLines.Length - MaxConsoleLines;
            panel.Children.Add(MakeTextBlock(MdSpec.Small, brushMuted, mono: false,
                text: string.Format("… 还有 {0} 行未显示", remaining)));

            var expand = new Button
            {
                Content = Loc.Get("Ui.ViewFullContent"),
                FontSize = MdSpec.Small,
                Cursor = Cursors.Hand,
                Padding = new Thickness(6, 2, 6, 2),
                Margin = new Thickness(0, 4, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            expand.Click += (_, _) =>
            {
                try
                {
                    panel.Children.Remove(expand);
                    for (int i = MaxConsoleLines; i < allLines.Length; i++)
                    {
                        panel.Children.Add(MakeTextBlock(MdSpec.Small, Theme("MdTextPrimary"), mono: true,
                            text: allLines[i]));
                    }
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[AMC] ExpandCollapse failed: {ex.Message}"); }
            };
            panel.Children.Add(expand);
        }
        return panel;
    }

    /// <summary>
    /// P0 (freeze fix, 2026-08-30): match a file-looking token in a bare console line.
    ///
    /// The previous pattern was <c>[A-Za-z0-9_.\-./\\]+\.[A-Za-z0-9]{1,6}</c> — its character class
    /// contains a literal dot AND is followed by an explicit <c>\.</c> plus a <c>{1,6}</c> suffix,
    /// so for path-like text (the red-boxed session is full of `/c/users/...` and dotted tokens)
    /// the engine tries every split of the `+` run against the trailing dot+ext. That is O(n²)×6
    /// catastrophic backtracking, and on a ~9KB message it stalled the UI thread indefinitely.
    ///
    /// Two independent fixes:
    ///  1. <see cref="RegexOptions.NonBacktracking"/> — the DFA engine guarantees LINEAR time
    ///     (it cannot backtrack at all), so no input can make it blow up.
    ///  2. The pattern is made unambiguous: a run of path chars that may not end in '.',
    ///     then '.', then 1-6 alnum. Callers additionally cap the scanned length.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex FileNamePatternCache =
        new(@"[A-Za-z0-9_\-\\/.]*[A-Za-z0-9_\-\\/]\.[A-Za-z0-9]{1,6}",
            System.Text.RegularExpressions.RegexOptions.NonBacktracking);
    private static System.Text.RegularExpressions.Regex FileNamePattern() => FileNamePatternCache;

    /// <summary>P0: hard cap on the line length we will scan for a file link. A filename longer
    /// than this is nonsense, and bounding it makes the per-line cost strictly O(1).</summary>
    private const int MaxLinkScanChars = 400;

    /// <summary>P0: hard cap on how many clickable file links a single message may produce.
    /// Prevents a pathological message from building thousands of Buttons/WrapPanels (each with
    /// its own measure/arrange) even if every line legitimately "looks like" a file.</summary>
    private const int MaxFileLinksPerMessage = 40;

    /// <summary>
    /// P0-3: cache of <c>File.Exists</c> results keyed by resolved absolute path.
    /// Tool output (ls / dir / glob) routinely contains hundreds of file-looking tokens, and
    /// without a cache we did one <b>synchronous disk query per token, on the UI thread</b> —
    /// hundreds of ms to seconds of freeze per render, re-paid on every re-parse. Results are
    /// memoized for the process lifetime; file creation between renders is an acceptable
    /// trade-off (a missed link is far cheaper than a frozen window).
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> FileExistsCache = new();

    /// <summary>
    /// ROOT CAUSE FIX (2026-08-30, red-boxed session freeze):
    /// The freeze was NOT the regex (regexMt=9ms) — it was <c>File.Exists</c> itself:
    /// <c>accessMs=22164</c> (22 SECONDS) spent on a SINGLE probe of a 240-char, non-Windows
    /// path. Win32 <c>GetFileAttributes</c> can block for tens of seconds on paths that look
    /// like WSL/POSIX form (<c>/c/users/...</c>), exceed MAX_PATH, or contain shell metacharacters
    /// — it attempts path normalization / network redirection before failing. The string-keyed
    /// cache below could not help because such paths occur only once each.
    ///
    /// Fix: reject anything that cannot possibly be a local Windows file using CHEAP string
    /// checks FIRST, and never touch the disk for those. This is O(1) and cannot block.
    /// </summary>
    private static bool LooksLikeProbeableWindowsPath(string fileName)
    {
        // Empty / whitespace / far too long to be a real path (MAX_PATH ≈ 260).
        if (string.IsNullOrWhiteSpace(fileName) || fileName.Length > 240) return false;

        // POSIX/WSL-style paths (/c/users/..., /bin/zsh, /usr/...) are NOT Windows local paths.
        // File.Exists on these is both pointless and pathologically slow.
        if (fileName[0] == '/') return false;

        // UNC or drive-relative must not contain characters illegal in Windows paths.
        if (fileName.IndexOfAny(System.IO.Path.GetInvalidPathChars()) >= 0) return false;
        // Shell metacharacters / quoting mean this is a command fragment, not a filename.
        if (fileName.IndexOfAny(new[] { '?', '*', '|', '<', '>', '"', '\'', '`', '$', ';', '&' }) >= 0) return false;

        // Must not be a bare drive/root, and must not be a UNC that we cannot trust.
        if (fileName.EndsWith(':')) return false;
        // UNC (\\server\share) — probing these can hit the network and block for a very long
        // time (SMB timeout). Only allow when it is clearly the local machine, otherwise skip.
        if (fileName.StartsWith(@"\\", StringComparison.Ordinal))
        {
            var rest = fileName.TrimStart('\\').Split('\\')[0];
            if (!string.Equals(rest, Environment.MachineName, StringComparison.OrdinalIgnoreCase)) return false;
        }
        // Cap the depth: a real file reference in tool output is at most a handful of segments;
        // a 20-segment "path" is almost certainly a match artifact and expensive to probe.
        int separators = 0;
        foreach (char c in fileName)
        {
            if (c == '\\' || c == '/') separators++;
        }
        if (separators > 6) return false;

        return true;
    }

    /// <summary>判断文件名是否指向本地可访问文件（用于决定是否渲染预览入口）。</summary>
    private static bool IsAccessibleFile(string fileName)
    {
        try
        {
            // P0 (freeze fix): cheap syntactic gate BEFORE any disk I/O. Paths that cannot be a
            // local Windows file (POSIX/WSL form, too long, illegal chars, deep, remote UNC) are
            // rejected outright — this is what eliminated the 22-second single-probe stall.
            if (!LooksLikeProbeableWindowsPath(fileName)) return false;

            // 相对路径基于当前工作目录；若文件存在则允许预览。
            var path = fileName;
            if (!System.IO.Path.IsPathRooted(path))
            {
                var baseDir = Environment.CurrentDirectory;
                path = System.IO.Path.Combine(baseDir, fileName);
            }
            // Defensive: Path.Combine can still produce an over-long path on a long CWD; and the
            // final absolute path must also be a sane length before we let the OS look at it.
            if (path is null || path.Length > 240) return false;

            // P0-3: memoize the disk probe — this is called once per file-looking token in the
            // output, which for a large tool result is hundreds of synchronous I/O calls.
            bool result = FileExistsCache.GetOrAdd(path, static p =>
            {
                try
                {
                    return System.IO.File.Exists(p);
                }
                catch
                {
                    return false;
                }
            });
            return result;
        }
        catch
        {
            return false;
        }
    }



    /// <summary>构建一个可点击的本地文件链接（预览 HTML / 打开 MD / 本地打开其他）。</summary>
    private UIElement BuildFileLink(string fileName)
    {
        bool isHtml = fileName.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
                      || fileName.EndsWith(".htm", StringComparison.OrdinalIgnoreCase);
        bool isMd = fileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                    || fileName.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase);

        var link = new Button
        {
            Content = fileName,
            FontSize = MdSpec.Small,
            FontFamily = MonospaceFont,
            Cursor = Cursors.Hand,
            Foreground = Theme("MdLinkForeground"),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0, 0, 0, 0),
            // html now opens in the system browser (see the click handler), so it shares the
            // "open locally" wording with other file types; only Markdown still previews in-app.
            ToolTip = isMd ? Loc.Get("Html.PreviewTitle") : Loc.Get("Msg.OpenLocally"),
        };
        link.Click += (_, _) =>
        {
            try
            {
                var path = fileName;
                if (!System.IO.Path.IsPathRooted(path))
                    path = System.IO.Path.Combine(Environment.CurrentDirectory, fileName);
                if (isMd)
                    // Markdown is rendered from its own text, with no sibling files or relative
                    // resources, so the in-app preview is reliable and keeps the user in-app.
                    HtmlPreviewWindow.OpenMarkdown(Loc.Get("Html.MdPreviewTitle"), System.IO.File.ReadAllText(path));
                else
                    // 本地 HTML（含 .html 站点）一律交给系统默认浏览器打开（2026-09-01 第三轮）。
                    //
                    // 曾在应用内 WebView2 预览，历经三种方案均告失败：
                    //   1. File.ReadAllText + NavigateToString → data: 源，页内相对链接无法解析
                    //   2. Navigate(file:///<file>)            → 页内链接能跳，但子资源被
                    //      WebView2 的 file:// 子资源策略屏蔽，子页面空白
                    //   3. SetVirtualHostNameToFolderMapping   → 转 https:// 源绕开子资源限制，
                    //      但对含非 ASCII（中文）的目录名在部分 WebView2 Runtime 上不生效
                    // 实测同一本地 html 用系统浏览器打开完全正常，http 外链也已走浏览器。
                    // 因此本地 HTML 与 http 链接统一走系统浏览器：行为一致，且浏览器处理
                    // 本地多页站点是其本职能力。
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[AMC] File link open failed: {ex.Message}"); }
        };
        return link;
    }

    /// <summary>渲染当前 <see cref="_blocks"/> 中前 <see cref="_maxBlocks"/> 个块；超出则给"展开更多"。</summary>
    private void RenderBlocks()
    {
        int count = Math.Min(_maxBlocks, _blocks.Count);
        for (int i = 0; i < count; i++)
        {
            var element = BuildBlock(_blocks[i]);
            if (element != null) _root.Children.Add(element);
        }

        int remaining = _blocks.Count - count;
        if (remaining > 0)
        {
            _root.Children.Add(BuildMoreButton(remaining));
        }
    }

    private UIElement BuildMoreButton(int remaining)
    {
        var more = new Button
        {
            Content = Loc.Format("Msg.ExpandMore", remaining),
            FontSize = 11,
            Foreground = Theme("MdLinkForeground"),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 4, 0, 0),
        };
        more.Click += (_, _) =>
        {
            // 仅加大上限并重新渲染，保持 reasoning，不重置上限。
            _maxBlocks += DefaultMaxBlocks;
            _root.Children.Clear();
            if (!string.IsNullOrWhiteSpace(Reasoning))
            {
                _root.Children.Add(BuildReasoningExpander(Reasoning));
            }
            RenderBlocks();
        };
        return more;
    }

    // ---- 块级构建 -------------------------------------------------------------

    private UIElement? BuildBlock(MarkdownBlock b) => b.Kind switch
    {
        MarkdownBlockKind.Paragraph => BuildParagraph(b),
        MarkdownBlockKind.Heading => BuildHeading(b),
        MarkdownBlockKind.ListItem => BuildListItem(b),
        MarkdownBlockKind.Quote => BuildQuote(b),
        MarkdownBlockKind.Code => BuildCode(b),
        MarkdownBlockKind.Table => BuildTable(b),
        MarkdownBlockKind.ThematicBreak => BuildThematicBreak(),
        _ => BuildParagraph(b),   // 兜底：新增未知 kind 按段落
    };

    private TextBlock BuildParagraph(MarkdownBlock b)
    {
        var tb = MakeTextBlock(MdSpec.Body, Theme("MdTextPrimary"));
        ApplyInlines(tb, b.Runs);
        return tb;
    }

    private UIElement BuildHeading(MarkdownBlock b)
    {
        var tb = MakeTextBlock(MdSpec.HeadingSize(b.Level ?? 1), Theme("MdTextPrimary"),
            FontWeight: FontWeights.SemiBold);
        ApplyInlines(tb, b.Runs);
        // 品牌色左条，强调层级
        var bar = new Border
        {
            BorderBrush = Theme("MdAccent"),
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(6, 2, 0, 2),
            Margin = new Thickness(0, 3, 0, 2),
            Child = tb,
        };
        return bar;
    }

    private UIElement BuildListItem(MarkdownBlock b)
    {
        var marker = MakeTextBlock(MdSpec.Body, Theme("MdAccent"), FontWeight: FontWeights.Bold);
        marker.Text = b.ListMarker;
        marker.Margin = new Thickness(0, 0, 4, 0);

        var content = MakeTextBlock(MdSpec.Body, Theme("MdTextPrimary"));
        ApplyInlines(content, b.Runs);

        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Margin = new Thickness((b.Level ?? 0) * 16, 1, 0, 1);
        panel.Children.Add(marker);
        panel.Children.Add(content);
        return panel;
    }

    private UIElement BuildQuote(MarkdownBlock b)
    {
        var tb = MakeTextBlock(MdSpec.Small, Theme("MdTextSecondary"), FontStyle: FontStyles.Italic);
        ApplyInlines(tb, b.Runs);
        return new Border
        {
            Background = Theme("MdQuoteBackground"),
            BorderBrush = Theme("MdBorder"),
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(8, 4, 8, 4),
            CornerRadius = new CornerRadius(0, 4, 4, 0),
            Margin = new Thickness(0, 2, 0, 2),
            Child = tb,
        };
    }

    private UIElement BuildCode(MarkdownBlock b)
    {
        // 深色代码块 + 品牌左条 + 顶部语言标签/复制按钮 + 主体滚动
        var body = MakeTextBlock(MdSpec.Code, Theme("MdCodeForeground"), mono: true);
        body.Text = b.Text ?? "";
        body.TextWrapping = TextWrapping.NoWrap;

        var scroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = MdSpec.CodeMaxHeight,
            Content = body,
        };

        // 语言标签 + 复制按钮（顶栏）
        var header = new DockPanel
        {
            LastChildFill = true,
            Background = Theme("MdCodeHeaderBackground"),
            Margin = new Thickness(0, 0, 0, 0),
        };
        var lang = MakeTextBlock(MdSpec.CodeHeader, Theme("MdCodeForeground"), mono: true);
        lang.Text = b.Language ?? "";
        lang.VerticalAlignment = VerticalAlignment.Center;
        lang.Visibility = string.IsNullOrWhiteSpace(lang.Text) ? Visibility.Collapsed : Visibility.Visible;
        DockPanel.SetDock(lang, Dock.Left);
        header.Children.Add(lang);

        var copy = new Button
        {
            Content = Loc.Get("Copy"),
            FontSize = MdSpec.CodeHeader,
            Padding = new Thickness(8, 1, 8, 1),
            Cursor = Cursors.Hand,
            Foreground = Theme("MdCodeForeground"),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
        };
        copy.Click += (_, _) =>
        {
            try { System.Windows.Clipboard.SetText(b.Text ?? ""); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[AMC] Clipboard copy failed: {ex.Message}"); }
        };
        DockPanel.SetDock(copy, Dock.Right);
        header.Children.Add(copy);

        // 问题2 + 建议4c：html/markdown 围栏代码块 → "预览"按钮。
        //   html → 系统浏览器（写临时文件，浏览器处理 file://+相对资源）；
        //   markdown → 应用内 Markdown 渲染（纯文本无外部依赖）。
        // AI 输出的围栏 html 经常是完整页面（DOCTYPE/head/script），data: 源下
        // script/external resources 全失败 → 预览窗口空白，故统一走浏览器。
        if (!string.IsNullOrWhiteSpace(b.Language))
        {
            bool isHtml = string.Equals(b.Language, "html", StringComparison.OrdinalIgnoreCase);
            bool isMd = string.Equals(b.Language, "md", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(b.Language, "markdown", StringComparison.OrdinalIgnoreCase);
            if (isHtml || isMd)
            {
                var preview = new Button
                {
                    Content = Loc.Get("Preview"),
                    FontSize = MdSpec.CodeHeader,
                    Padding = new Thickness(8, 1, 8, 1),
                    Margin = new Thickness(0, 0, 4, 0),
                    Cursor = Cursors.Hand,
                    Foreground = Theme("MdCodeForeground"),
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                };
                preview.Click += (_, _) =>
                {
                    if (isHtml)
                        OpenHtmlInSystemBrowser(b.Text ?? "", Loc.Get("Html.PreviewTitle"));
                    else
                        HtmlPreviewWindow.OpenMarkdown(Loc.Get("Html.MdPreviewTitle"), b.Text ?? "");
                };
                DockPanel.SetDock(preview, Dock.Right);
                header.Children.Add(preview);
            }
        }

        var headerBorder = new Border
        {
            Background = Theme("MdCodeHeaderBackground"),
            BorderBrush = Theme("MdCodeBorder"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(8, 3, 8, 3),
            Child = header,
        };

        var stack = new StackPanel();
        stack.Children.Add(headerBorder);
        stack.Children.Add(scroller);

        return new Border
        {
            Background = Theme("MdCodeBackground"),
            BorderBrush = Theme("MdAccent"),
            BorderThickness = new Thickness(3, 0, 0, 0),
            CornerRadius = new CornerRadius(MdSpec.CodeCorner),
            Margin = new Thickness(0, 2, 0, 2),
            Child = stack,
        };
    }

    /// <summary>
    /// 2026-09-01: HTML 围栏块的"预览"按钮改走浏览器（与 BuildFileLink 的本地 .html
    /// 一致）。将字符串写到 %TEMP% 下唯一的 .html 文件，用 ShellExecute=true 启动——浏览器
    /// 打开 file:// 源能正常处理 script/css/external resources，且与之前"链接走浏览器"
    /// 的行为一致，用户体验统一。AI 输出的围栏 html 经常是完整页面（DOCTYPE/head/script），
    /// data: 源下 script 与外部资源全部失败 → 应用内预览窗口空白，故统一走浏览器。
    /// </summary>
    private static void OpenHtmlInSystemBrowser(string html, string? titleHint)
    {
        try
        {
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dsh-html-preview");
            System.IO.Directory.CreateDirectory(dir);
            // Random file name avoids parallel preview collisions and keeps browsers from
            // sharing a cached render between previews.
            var name = $"preview-{System.DateTime.UtcNow:yyyyMMddHHmmssfff}-{System.Guid.NewGuid():N}.html";
            var fullPath = System.IO.Path.Combine(dir, name);
            System.IO.File.WriteAllText(fullPath, html);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(fullPath)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AssistantMessageControl] open html in browser failed: {ex.Message}");
        }
    }

    private UIElement BuildTable(MarkdownBlock b)
    {
        if (b.Rows == null || b.Rows.Count == 0)
        {
            return MakeTextBlock(MdSpec.Small, Theme("MdTextSecondary"));
        }

        var outer = new Border
        {
            BorderBrush = Theme("MdBorder"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Margin = new Thickness(0, 2, 0, 2),
            // 表格自适应气泡宽度，但有最大宽度上限保持可读性
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MaxWidth = 900,
        };

        var rows = new StackPanel();
        foreach (var row in b.Rows)
        {
            var grid = new UniformGrid { Rows = 1, Columns = Math.Max(1, row.Cells.Length) };
            var rowBg = row.IsHeader ? Theme("MdTableHeaderBackground") : Brushes.Transparent;
            for (int i = 0; i < row.Cells.Length; i++)
            {
                var cell = MakeTextBlock(MdSpec.Small, Theme("MdTextPrimary"), mono: true);
                cell.Text = row.Cells[i];
                cell.TextWrapping = TextWrapping.Wrap;
                cell.Padding = new Thickness(6, 3, 6, 3);
                cell.FontWeight = row.IsHeader ? FontWeights.SemiBold : FontWeights.Normal;
                cell.Background = rowBg;
                // TextBlock 没有 Border，用外层 Border 画单元格分隔线。
                grid.Children.Add(new Border
                {
                    BorderBrush = Theme("MdBorder"),
                    BorderThickness = new Thickness(0, 0, 1, 1),
                    Child = cell,
                });
            }
            rows.Children.Add(grid);
        }
        outer.Child = rows;
        return outer;
    }

    private UIElement BuildThematicBreak() => new Border
    {
        Background = Theme("MdBorder"),
        Height = 1,
        Margin = new Thickness(0, 6, 0, 6),
    };

    // ---- Reasoning 折叠 ---------------------------------------------------------

    private UIElement BuildReasoningExpander(string reasoning)
    {
        var content = MakeTextBlock(MdSpec.Small, Theme("MdTextSecondary"), FontStyle: FontStyles.Italic);
        content.Text = reasoning;
        content.TextWrapping = TextWrapping.Wrap;

        var expander = new Expander
        {
            Header = Loc.Get("Reasoning"),
            Foreground = Theme("MdTextSecondary"),
            FontWeight = FontWeights.SemiBold,
            FontSize = 11,
            Margin = new Thickness(0, 0, 0, 4),
            IsExpanded = false,
        };
        var body = new Border
        {
            Background = Theme("MdQuoteBackground"),
            Padding = new Thickness(8, 6, 8, 6),
            CornerRadius = new CornerRadius(4),
            Child = content,
        };
        expander.Content = body;
        return expander;
    }

    // ---- 行内富文本 -------------------------------------------------------------

    private void ApplyInlines(TextBlock tb, IReadOnlyList<InlineRun>? runs)
    {
        if (runs == null) return;
        foreach (var run in runs)
        {
            tb.Inlines.Add(BuildInline(run));
        }
    }

    private Inline BuildInline(InlineRun run)
    {
        switch (run.Kind)
        {
            case InlineKind.Bold:
                return new Bold(new Run(run.Text));
            case InlineKind.Italic:
                return new Italic(new Run(run.Text));
            case InlineKind.InlineCode:
                return new Run(run.Text)
                {
                    FontFamily = MonospaceFont,
                    Background = Theme("MdInlineCodeBackground"),
                    Foreground = Theme("MdInlineCodeForeground"),
                };
            case InlineKind.Link:
                return MakeHyperlink(run.Text, run.Url, isFile: false);
            case InlineKind.File:
                return MakeHyperlink(run.Text, run.Text, isFile: true);
            default:
                return new Run(run.Text);
        }
    }

    private Hyperlink MakeHyperlink(string text, string? target, bool isFile)
    {
        var link = new Hyperlink(new Run(text))
        {
            Foreground = Theme("MdLinkForeground"),
        };
        // 设置 NavigateUri 以触发 RequestNavigate（点击后拦截，路由到文件/浏览器）。
        if (!string.IsNullOrWhiteSpace(target))
        {
            try
            {
                link.NavigateUri = isFile
                    ? new Uri("file:///" + target, UriKind.RelativeOrAbsolute)
                    : new Uri(target, UriKind.RelativeOrAbsolute);
            }
            catch
            {
                link.NavigateUri = null;
            }
        }
        HyperlinkData.SetIsFile(link, isFile);
        link.RequestNavigate += (_, e) =>
        {
            e.Handled = true;
            MdNavigate.Open(target, isFile);
        };
        return link;
    }

    // ---- 主题与工具 -------------------------------------------------------------

    private static readonly FontFamily MonospaceFont = new("Cascadia Mono, Consolas");

    private Brush Theme(string key)
    {
        var b = TryFindResource(key) as Brush ?? Brushes.Gray;
        return b;
    }

    private static TextBlock MakeTextBlock(double fontSize, Brush foreground,
        FontStyle? FontStyle = null, FontWeight? FontWeight = null, bool mono = false, string? text = null)
    {
        var tb = new TextBlock
        {
            FontSize = fontSize,
            Foreground = foreground,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = fontSize * MdSpec.LineHeight,
        };
        if (FontStyle.HasValue) tb.FontStyle = FontStyle.Value;
        if (FontWeight.HasValue) tb.FontWeight = FontWeight.Value;
        if (mono) tb.FontFamily = MonospaceFont;
        if (text != null) tb.Text = text;
        return tb;
    }
}
