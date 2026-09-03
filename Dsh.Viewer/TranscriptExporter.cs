using System.Collections.Generic;
using System.IO;
using System.Text;
using Dsh.App;
using Localization = Dsh.App.Services.Localization;

namespace Dsh.Viewer;

/// <summary>
/// S4c export: renders the loaded session as a Markdown transcript (role-labelled, incl.
/// reasoning and tool cards) or writes the original raw JSONL (faithfully, decompressing zstd
/// on the fly). Pure logic (no UI) so it is unit-testable; os/03 L4.
/// </summary>
public static class TranscriptExporter
{
    /// <summary>Render the folded surface into a Markdown transcript string.</summary>
    public static string ToMarkdown(string sessionTitle, IEnumerable<SessionFold.Row> rows)
    {
        var sb = new StringBuilder();
        string shown = string.IsNullOrWhiteSpace(sessionTitle)
            ? Localization.Get("Viewer.ExportUntitled") : sessionTitle;
        sb.Append(Localization.Format("Viewer.ExportTranscriptTitle", shown)).AppendLine();
        sb.AppendLine();

        foreach (var row in rows)
        {
            switch (row.Role)
            {
                case "user":
                    sb.Append(Localization.Get("Viewer.ExportSection.User")).AppendLine();
                    break;
                case "assistant":
                    sb.Append(Localization.Get("Viewer.ExportSection.Assistant")).AppendLine();
                    break;
                case "tool":
                    AppendTool(sb, row);
                    continue; // AppendTool already wrote its section + blank line
                case "error":
                    sb.Append(Localization.Get("Viewer.ExportSection.Error")).AppendLine();
                    break;
                case "turn":
                    sb.AppendLine().Append("---").AppendLine().Append("_").Append(row.Text).AppendLine("_").AppendLine();
                    continue;
                default:
                    continue;
            }

            if (!string.IsNullOrWhiteSpace(row.Reasoning))
            {
                sb.Append("<details><summary>").Append(Localization.Get("Viewer.ExportReasoningSummary")).AppendLine("</summary>");
                sb.AppendLine().AppendLine();
                sb.Append(row.Reasoning).AppendLine();
                sb.AppendLine("</details>").AppendLine();
            }
            if (!string.IsNullOrWhiteSpace(row.Text))
            {
                sb.AppendLine(row.Text).AppendLine();
            }
        }
        return sb.ToString();
    }

    private static void AppendTool(StringBuilder sb, SessionFold.Row row)
    {
        var t = row.Tool;
        sb.Append(Localization.Format("Viewer.ExportSection.Tool", t?.Name ?? "", t?.Status ?? "")).AppendLine();
        if (t is { Arguments.Length: > 0 })
        {
            sb.Append("```json").AppendLine();
            sb.AppendLine(t.Arguments);
            sb.Append("```").AppendLine();
        }
        if (t is { Output.Length: > 0 })
        {
            sb.Append("<details><summary>").Append(Localization.Get("Viewer.ExportToolResultSummary")).AppendLine("</summary>");
            sb.Append("```").AppendLine();
            sb.AppendLine(t.Output);
            sb.Append("```").AppendLine();
            sb.AppendLine("</details>").AppendLine();
        }
        if (t is { Children.Count: > 0 })
        {
            sb.AppendLine();
            foreach (var c in t.Children) AppendTool(sb, WrapToolRow(c));
            sb.AppendLine();
        }
    }

    private static SessionFold.Row WrapToolRow(SessionFold.ToolCallNode c) =>
        new("tool", "") { Tool = c };

    /// <summary>
    /// Write a session to Markdown at <paramref name="destPath"/>.
    /// </summary>
    public static void ExportMarkdown(string sessionTitle, IEnumerable<SessionFold.Row> rows, string destPath)
    {
        File.WriteAllText(destPath, ToMarkdown(sessionTitle, rows), Encoding.UTF8);
    }

    /// <summary>
    /// Copy the original raw JSONL to <paramref name="destPath"/>, decompressing a .zstd source
    /// on the fly so the exported file is plain, human-readable JSONL (faithful to disk records).
    /// </summary>
    public static void ExportRawJsonl(string srcPath, bool isZstd, string destPath)
    {
        using var dst = new StreamWriter(destPath, append: false, Encoding.UTF8);
        foreach (var line in ZstdReader.ReadLines(srcPath, isZstd))
        {
            dst.WriteLine(line);
        }
    }
}
