namespace Dsh.App;

partial class SessionFold
{
    /// <summary>One surface row: role, text, optional reasoning, optional tool call, message id, time.</summary>
    public sealed record Row(
        string Role,
        string Text,
        string? Reasoning = null,
        ToolCallNode? Tool = null,
        string? MessageId = null,
        long? Time = null);
}
