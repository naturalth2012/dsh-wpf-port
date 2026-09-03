using System.Text;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Dsh.App;

/// <summary>
/// One inline run inside a <see cref="MarkdownBlock"/>: plain text, emphasis, inline code,
/// a hyperlink, or a clickable file path (H4). The view renders <see cref="InlineKind"/> distinctly.
/// </summary>
public enum InlineKind { Text, Bold, Italic, InlineCode, Link, File }

/// <summary>A single rich-text run within a block (M0). <c>Url</c> is set for links.</summary>
public sealed record InlineRun(InlineKind Kind, string Text, string? Url = null);

/// <summary>One table cell row; <c>IsHeader</c> marks the header row (GFM pipe tables).</summary>
public sealed record MarkdownCell(string[] Cells, bool IsHeader);

/// <summary>
/// Strongly-typed block kind (A1). The view switches on this enum; the compiler enforces that
/// every kind is handled (no more string magic → silent blank).
/// </summary>
public enum MarkdownBlockKind { Paragraph, Heading, ListItem, Quote, Code, Table, ThematicBreak }

/// <summary>
/// A projected block of rendered Markdown (M0/A1). <see cref="MarkdownBlockKind"/> drives the
/// renderer; the view picks the visual per kind and renders <c>Runs</c> (rich inline),
/// <c>Rows</c>/<c>Lines</c>/<c>Text</c> as appropriate.
/// </summary>
public sealed record MarkdownBlock(
    MarkdownBlockKind Kind,
    string? Text = null,
    IReadOnlyList<InlineRun>? Runs = null,
    string? Language = null,
    IReadOnlyList<string>? Lines = null,
    IReadOnlyList<MarkdownCell>? Rows = null,
    int? Level = null,
    string? ListMarker = null);

/// <summary>
/// Renders a Markdown string into UI-bindable <see cref="MarkdownBlock"/>s using Markdig (M0).
/// This replaces the shallow <see cref="MarkdownSegmenter"/> (which only split code/quote/text)
/// with full GFM parsing: headings, bold/italic, lists, pipe tables, fenced code, links, and
/// blockquotes. Pure logic (no WPF dependency) so it lives in <c>Dsh.App</c> and is unit-testable.
/// </summary>
public static class MarkdownRenderer
{
    // GFM pipe tables require explicit pipeline configuration; the default CommonMark pipeline
    // does not include them.
    //
    // 2026-09-01: exposed as ToHtml's engine so the detail window (HtmlPreviewWindow) renders
    // with the SAME pipeline as the inline transcript. Before this, the popup used a hand-rolled
    // Markdown subset converter that had no GFM table support — pipe-table rows fell through to
    // plain <p> paragraphs and showed as literal "| a | b |" text (reported as "弹出框显示格式
    // 有问题"). One engine, one feature set, everywhere.
    public static readonly MarkdownPipeline Pipeline =
        new MarkdownPipelineBuilder().UsePipeTables().Build();

    /// <summary>
    /// Render markdown to HTML with the SAME pipeline the inline transcript uses (GFM pipe
    /// tables etc.), so the detail window always matches the inline view. The caller supplies
    /// its own CSS; this method returns the fragment only (no &lt;html&gt; wrapper).
    /// </summary>
    /// <remarks>
    /// Runs on the caller's thread. For the detail window that is fine: it is a one-shot user
    /// action and the input is bounded by <see cref="MaxParseBytes"/> (see <see cref="Render"/>'s
    /// oversized-input guard — inputs beyond it render as a single plain paragraph).
    /// </remarks>
    public static string ToHtml(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return "";
        if (markdown.Length > MaxParseBytes)
        {
            // Same oversized guard as Render: escape and emit as one paragraph, no Markdig run.
            return "<p>" + System.Net.WebUtility.HtmlEncode(markdown) + "</p>";
        }
        var doc = Markdig.Markdown.Parse(markdown, Pipeline);
        var writer = new System.IO.StringWriter();
        var renderer = new Markdig.Renderers.HtmlRenderer(writer);
        Pipeline.Setup(renderer);
        renderer.Render(doc);
        writer.Flush();
        return writer.ToString();
    }

    /// <summary>Max input length for Markdig parsing; beyond this we skip Markdown and render
    /// plain text (defense against UI-thread stall on huge tool outputs).</summary>
    private const int MaxParseBytes = 512 * 1024;

    /// <summary>Parse <paramref name="markdown"/> and project it to a flat block list.</summary>
    public static IReadOnlyList<MarkdownBlock> Render(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return [];
        // 超长保护（2026-08-24 卡死根因）：Markdig 对超大输入（glob 命中上千路径、超长
        // tool 结果）的同步解析在 UI 线程可秒级到分钟级。超过阈值直接返回一个纯文本段落，
        // 放弃 Markdown 排版以保住 UI 响应。配合 AssistantMessageControl 的 256KB 阈值兜底。
        if (markdown.Length > MaxParseBytes)
        {
            return [new MarkdownBlock(MarkdownBlockKind.Paragraph, Text: markdown)];
        }
        var doc = Markdig.Markdown.Parse(markdown, Pipeline);
        var result = new List<MarkdownBlock>();
        WalkBlocks(doc, 0, result);
        return result;
    }

    // ---- Block-level projection -------------------------------------------------------

    private static void WalkBlocks(ContainerBlock parent, int depth, List<MarkdownBlock> outBlocks)
    {
        foreach (var block in parent)
        {
            switch (block)
            {
                case ParagraphBlock p:
                    outBlocks.Add(new MarkdownBlock(MarkdownBlockKind.Paragraph, Runs: Flatten(p.Inline)));
                    break;
                case HeadingBlock h:
                    outBlocks.Add(new MarkdownBlock(MarkdownBlockKind.Heading, Runs: Flatten(h.Inline), Level: h.Level));
                    break;
                case FencedCodeBlock f:
                    AddCode(f, outBlocks);
                    break;
                case CodeBlock c:
                    // Indented code blocks land here (FencedCodeBlock handled above).
                    outBlocks.Add(new MarkdownBlock(MarkdownBlockKind.Code, Text: c.Lines.ToString().TrimEnd('\r', '\n')));
                    break;
                case QuoteBlock q:
                    outBlocks.Add(new MarkdownBlock(MarkdownBlockKind.Quote, Runs: FlattenContainer(q)));
                    break;
                case ThematicBreakBlock:
                    outBlocks.Add(new MarkdownBlock(MarkdownBlockKind.ThematicBreak));
                    break;
                case ListBlock list:
                    WalkList(list, depth, outBlocks);
                    break;
                case Table table:
                    outBlocks.Add(BuildTable(table));
                    break;
                case HtmlBlock html:
                    // Best-effort: expose raw HTML body as plain text (we never execute it).
                    outBlocks.Add(new MarkdownBlock(MarkdownBlockKind.Paragraph, Text: html.Lines.ToString().Trim()));
                    break;
                default:
                    // Fallback: expose any nested inline as a paragraph.
                    if (block is LeafBlock lb && lb.Inline != null)
                    {
                        outBlocks.Add(new MarkdownBlock(MarkdownBlockKind.Paragraph, Runs: Flatten(lb.Inline)));
                    }
                    break;
            }
        }
    }

    private static void AddCode(FencedCodeBlock f, List<MarkdownBlock> outBlocks)
    {
        string? lang = null;
        var info = f.Info?.Trim();
        if (!string.IsNullOrWhiteSpace(info))
        {
            lang = info.Split(' ', '\t')[0];
        }
        outBlocks.Add(new MarkdownBlock(MarkdownBlockKind.Code,
            Text: f.Lines.ToString().TrimEnd('\r', '\n'),
            Language: lang));
    }

    private static void WalkList(ListBlock list, int depth, List<MarkdownBlock> outBlocks)
    {
        bool ordered = list.IsOrdered;
        int index = int.TryParse(list.OrderedStart, out var start) ? start : 1;
        foreach (var item in list)
        {
            if (item is not ListItemBlock li) continue;
            string marker = ordered ? $"{index}. " : "• ";
            var runs = new List<InlineRun>();
            foreach (var sub in li)
            {
                if (sub is ListBlock) continue; // nested lists are recursed below
                if (sub is LeafBlock leaf && leaf.Inline != null)
                {
                    runs.AddRange(Flatten(leaf.Inline));
                }
                else if (sub is ContainerBlock cb)
                {
                    runs.AddRange(FlattenContainer(cb));
                }
            }
            outBlocks.Add(new MarkdownBlock(MarkdownBlockKind.ListItem, Runs: runs, Level: depth, ListMarker: marker));
            foreach (var sub in li)
            {
                if (sub is ListBlock nested)
                {
                    WalkList(nested, depth + 1, outBlocks);
                }
            }
            index++;
        }
    }

    private static MarkdownBlock BuildTable(Table table)
    {
        var rows = new List<MarkdownCell>();
        foreach (var block in table)
        {
            if (block is not TableRow row) continue;
            var cells = new List<string>();
            foreach (var cellBlock in row)
            {
                var text = new StringBuilder();
                if (cellBlock is LeafBlock lb && lb.Inline != null)
                {
                    text.Append(PlainText(lb.Inline));
                }
                else if (cellBlock is ContainerBlock cb)
                {
                    // A TableCell may wrap its content in a nested paragraph block.
                    foreach (var inner in cb)
                    {
                        if (inner is LeafBlock ilb && ilb.Inline != null)
                        {
                            text.Append(PlainText(ilb.Inline));
                        }
                    }
                }
                cells.Add(text.ToString().Trim());
            }
            rows.Add(new MarkdownCell(cells.ToArray(), row.IsHeader));
        }
        return new MarkdownBlock(MarkdownBlockKind.Table, Rows: rows);
    }

    /// <summary>Flatten a block container (e.g. a quote) into inline runs for simple rendering.</summary>
    private static IReadOnlyList<InlineRun> FlattenContainer(ContainerBlock block)
    {
        var result = new List<InlineRun>();
        foreach (var child in block)
        {
            if (child is LeafBlock lb && lb.Inline != null)
            {
                var runs = Flatten(lb.Inline);
                if (runs.Count > 0)
                {
                    result.AddRange(runs);
                    result.Add(new InlineRun(InlineKind.Text, "\n"));
                }
            }
            else if (child is ContainerBlock cb)
            {
                result.AddRange(FlattenContainer(cb));
            }
        }
        while (result.Count > 0 && result[^1].Text == "\n")
        {
            result.RemoveAt(result.Count - 1);
        }
        return result;
    }

    // ---- Inline-level projection ------------------------------------------------------

    /// <summary>Flatten a leaf's inline container (e.g. paragraph, heading) into runs.</summary>
    private static IReadOnlyList<InlineRun> Flatten(ContainerInline? container)
    {
        var result = new List<InlineRun>();
        if (container == null) return result;
        WalkInlines(container.FirstChild, InlineKind.Text, null, result);
        return result;
    }

    private static void WalkInlines(Inline? node, InlineKind style, string? linkUrl, List<InlineRun> result)
    {
        for (var cur = node; cur != null; cur = cur.NextSibling)
        {
            switch (cur)
            {
                case LiteralInline lit:
                    AddText(result, style, linkUrl, lit.Content.ToString());
                    break;
                case CodeInline code:
                    var content = code.Content.ToString();
                    var kind = MarkdownSegmenter.IsFilePath(content) ? InlineKind.File : InlineKind.InlineCode;
                    result.Add(new InlineRun(kind, content));
                    break;
                case EmphasisInline emph:
                    var s = emph.DelimiterCount >= 2 ? InlineKind.Bold : InlineKind.Italic;
                    WalkInlines(emph.FirstChild, s, linkUrl, result);
                    break;
                case LinkInline link:
                    // Images are exposed as plain links for now (no image renderer).
                    WalkInlines(link.FirstChild, InlineKind.Link, link.Url?.ToString(), result);
                    break;
                case LineBreakInline:
                    result.Add(new InlineRun(InlineKind.Text, "\n"));
                    break;
                default:
                    if (cur is ContainerInline ci)
                    {
                        WalkInlines(ci.FirstChild, style, linkUrl, result);
                    }
                    break;
            }
        }
    }

    private static void AddText(List<InlineRun> result, InlineKind style, string? linkUrl, string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var kind = linkUrl != null ? InlineKind.Link : style;
        if (result.Count > 0 && result[^1].Kind == kind && result[^1].Url == linkUrl)
        {
            result[^1] = result[^1] with { Text = result[^1].Text + text };
        }
        else
        {
            result.Add(new InlineRun(kind, text, linkUrl));
        }
    }

    /// <summary>Concatenate a container's plain text (used for table cells / summary).</summary>
    private static string PlainText(ContainerInline container)
    {
        var sb = new StringBuilder();
        WalkPlain(container.FirstChild, sb);
        return sb.ToString();
    }

    private static void WalkPlain(Inline? node, StringBuilder sb)
    {
        for (var n = node; n != null; n = n.NextSibling)
        {
            switch (n)
            {
                case LiteralInline lit: sb.Append(lit.Content.ToString()); break;
                case CodeInline code: sb.Append(code.Content.ToString()); break;
                case LineBreakInline: sb.Append(' '); break;
                case ContainerInline ci: WalkPlain(ci.FirstChild, sb); break;
            }
        }
    }
}
