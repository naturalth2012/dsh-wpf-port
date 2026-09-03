using System.Windows;
using System.Windows.Controls;
using Dsh.App;
using Application = System.Windows.Application;

namespace Dsh.Wpf;

/// <summary>
/// Selects a per-type tool card <see cref="DataTemplate"/> based on the tool call's <c>Name</c>
/// (P0-3): terminal / read / diff / search / web / todo / code get a tailored template; unknown
/// names fall back to the generic JSON card. Templates are resolved by resource key from the
/// visual container so the selector stays declarative and testable.
/// </summary>
public sealed class ToolCardTemplateSelector : DataTemplateSelector
{
    /// <summary>Resource key of the fallback (generic) template.</summary>
    public string FallbackKey { get; set; } = "GenericToolTemplate";

    public override DataTemplate? SelectTemplate(object item, DependencyObject container)
    {
        if (item is not SessionFold.ToolCallNode node) return null;

        string key = MatchKey(node.Name) ?? FallbackKey;
        if (container is FrameworkElement fe && fe.TryFindResource(key) is DataTemplate t)
        {
            return t;
        }
        // Fall back to the window/app level resource when the container chain lacks it.
        return Application.Current?.TryFindResource(key) as DataTemplate;
    }

    /// <summary>
    /// Map a tool name to a card template key. C4: all non-diff harness tools share the standard
    /// card (code/terminal/read/write/search/web/todo were previously 5 byte-identical templates);
    /// only <c>diff</c> keeps a tailored template (green/red line markers). Unknown names return
    /// null → caller falls back to <see cref="FallbackKey"/> (the generic JSON card).
    /// </summary>
    internal static string? MatchKey(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        string lower = name.Trim().ToLowerInvariant();

        if (lower.StartsWith("diff"))
        {
            return "DiffToolTemplate";
        }
        if (lower.StartsWith("terminal") || lower == "bash" || lower == "sh" ||
            lower.StartsWith("run") || lower.StartsWith("code") || lower.Contains("exec") ||
            lower.StartsWith("read") || lower.StartsWith("write") || lower.StartsWith("edit") ||
            lower.Contains("file") ||
            lower.StartsWith("search") || lower.StartsWith("grep") || lower.StartsWith("glob") ||
            lower.StartsWith("find") ||
            lower.Contains("web") || lower.StartsWith("http") || lower.StartsWith("fetch") ||
            lower.StartsWith("todo"))
        {
            return "StandardToolTemplate";
        }
        return null;
    }
}
