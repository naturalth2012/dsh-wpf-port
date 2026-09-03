using CommunityToolkit.Mvvm.ComponentModel;
using Dsh.App;

namespace Dsh.Wpf;

/// <summary>One transcript line; <c>Role</c> is user/assistant/tool/system/error.</summary>
public partial class ChatEntry : ObservableObject
{
    public string Role { get; }

    [ObservableProperty]
    private string text;

    /// <summary>Optional reasoning/think block shown folded above the main text.</summary>
    [ObservableProperty]
    private string? reasoning;

    /// <summary>Optional tool-call node (role = "tool"), carrying output and status.</summary>
    [ObservableProperty]
    private SessionFold.ToolCallNode? tool;

    /// <summary>Message id for feedback attachment (H5, P2-4); null when the row has no id.</summary>
    public string? MessageId { get; }

    /// <summary>Event time (epoch ms) for hover timestamps; null when the row has no time.</summary>
    public long? Time { get; }

    /// <summary>
    /// True when this row starts a new assistant "turn" (the first assistant message after a
    /// user/tool boundary). The view uses it to show the role avatar only once per turn (M1).
    /// Set by <see cref="MainViewModel.Messages"/> when it builds the transcript window.
    /// </summary>
    public bool IsTurnStart { get; set; }

    /// <summary>
    /// Markdown is rendered by <see cref="Controls.AssistantMessageControl"/> directly from
    /// <see cref="Text"/> (via its TextProperty DP), so ChatEntry no longer projects blocks.
    /// </summary>
    public ChatEntry(string role, string text, string? reasoning = null, SessionFold.ToolCallNode? tool = null, string? messageId = null, long? time = null)
    {
        Role = role;
        this.text = text;
        this.reasoning = reasoning;
        this.tool = tool;
        MessageId = messageId;
        Time = time;
    }
}
