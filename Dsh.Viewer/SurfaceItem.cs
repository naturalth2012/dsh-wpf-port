using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Dsh.App;
using Localization = Dsh.App.Services.Localization;

namespace Dsh.Viewer;

/// <summary>
/// A display wrapper around one <see cref="SessionFold.Row"/> for the offline Surface view.
/// Precomputes role-localized label, a <c>RawJson</c> summary for the per-item detail panel
/// (SessionFold does not retain the original disk line per row, so we project the folded fields),
/// and keeps the row for <see cref="MessageRowControl"/> binding.
/// </summary>
public sealed class SurfaceItem
{
    public SurfaceItem(SessionFold.Row row, int index)
    {
        Row = row;
        Index = index;
    }

    public SessionFold.Row Row { get; }

    /// <summary>0-based position in the surface list (for the detail header).</summary>
    public int Index { get; }

    public string RoleLabel => Role switch
    {
        "user" => Localization.Get("Viewer.Role.User"),
        "assistant" => Localization.Get("Viewer.Role.Assistant"),
        "tool" => Localization.Get("Viewer.Role.Tool"),
        "error" => Localization.Get("Viewer.Role.Error"),
        "turn" => Localization.Get("Viewer.Role.Turn"),
        _ => Role,
    };

    public string Role => Row.Role;

    /// <summary>
    /// A JSON summary of the row's folded fields, shown in the per-item Raw detail panel.
    /// Built lazily once and cached.
    /// </summary>
    public string RawJson
    {
        get
        {
            _rawJson ??= BuildRawJson();
            return _rawJson;
        }
    }
    private string? _rawJson;

    private string BuildRawJson()
    {
        var tool = Row.Tool;
        var root = new Dictionary<string, object?>
        {
            ["index"] = Index,
            ["role"] = Row.Role,
            ["time"] = Row.Time,
            ["messageId"] = Row.MessageId,
            ["text"] = Row.Text,
            ["reasoning"] = Row.Reasoning,
        };
        if (tool is not null)
        {
            root["tool"] = SerializeTool(tool);
        }
        return JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true });
    }

    private static object SerializeTool(SessionFold.ToolCallNode t) => new
    {
        callId = t.CallId,
        name = t.Name,
        arguments = t.Arguments,
        status = t.Status,
        output = Truncate(t.Output),
        children = t.Children is { Count: > 0 } ? t.Children.Select(SerializeTool).ToArray() : null,
    };

    private static string? Truncate(string? s)
    {
        if (s is null) return null;
        const int max = 2000;
        return s.Length <= max ? s : s[..max] + Localization.Get("Viewer.Truncated");
    }
}
