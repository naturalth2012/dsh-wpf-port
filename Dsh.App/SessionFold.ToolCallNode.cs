using System.Text.Json;

namespace Dsh.App;

partial class SessionFold
{
    /// <summary>A tool-call node in the tree; paired by <c>CallId</c> between call and result.</summary>
    public sealed record ToolCallNode(
        string CallId,
        string Name,
        string? Arguments,
        string Status,
        string? Output,
        IReadOnlyList<ToolCallNode>? Children = null)
    {
        public ToolCallNode WithResult(string status, string? output) =>
            this with { Status = status, Output = output };

        /// <summary>
        /// Output split into classified diff lines for the diff card (P0-3). Null when there is
        /// no output. Each line is a <see cref="DiffLine"/> with a precomputed <c>Kind</c>
        /// (add/remove/header/context) so the view can color it without a converter.
        /// </summary>
        public IReadOnlyList<DiffLine>? DiffLines
        {
            get
            {
                if (string.IsNullOrEmpty(Output)) return null;
                return Output.Split('\n').Select(DiffLine.FromText).ToArray();
            }
        }

        /// <summary>
        /// Build a recursive tool node from a model `tool-call` content block, mirroring
        /// <c>ToolCallBlock</c>: a running call (callId/name/argsRaw) or a settled result
        /// (kind 'tool-result', callId, isError, content), each owning <c>subCalls</c>.
        /// </summary>
        public static ToolCallNode FromBlock(JsonElement block)
        {
            string kind = block.TryGetProperty("kind", out var k) ? k.GetString() ?? "" : "";
            string callId = block.TryGetProperty("callId", out var c) ? c.GetString() ?? "" : "";
            string name = block.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            string args = block.TryGetProperty("argsRaw", out var a) && a.ValueKind == JsonValueKind.String
                ? a.GetString() ?? ""
                : "";

            // Settled result: { kind:'tool-result', callId, call:{name}, isError, content, subCalls }.
            bool isSettled = kind == "tool-result";
            if (isSettled && string.IsNullOrEmpty(name))
            {
                name = block.TryGetProperty("call", out var call) && call.TryGetProperty("name", out var cn)
                    ? cn.GetString() ?? ""
                    : "";
            }
            string status = isSettled
                ? (block.TryGetProperty("isError", out var ie) && ie.GetBoolean() ? "error" : "done")
                : "running";
            string? output = null;
            if (isSettled && block.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            {
                output = ExtractTextBlocks(content);
            }

            var children = new List<ToolCallNode>();
            if (block.TryGetProperty("subCalls", out var subs) && subs.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in subs.EnumerateArray())
                {
                    children.Add(FromBlock(child));
                }
            }

            return new ToolCallNode(callId, name, args, status, output,
                children.Count == 0 ? null : children);
        }
    }

    /// <summary>
    /// A single diff line with a precomputed classification for the diff tool card (P0-3):
    /// <c>add</c> (leading "+"), <c>remove</c> (leading "-"), <c>header</c> ("@@" / "diff " /
    /// "Index" / "+++" / "---"), else <c>context</c>.
    /// </summary>
    public sealed record DiffLine(string Text, string Kind)
    {
        public static DiffLine FromText(string line)
        {
            // Header markers must be checked before the single-char +/- (a "+++" or "---"
            // line is a diff header, not an add/remove line).
            string kind = line.StartsWith("@@") || line.StartsWith("diff ") ||
                          line.StartsWith("Index") || line.StartsWith("+++") || line.StartsWith("---")
                ? "header"
                : line.StartsWith('+')
                    ? "add"
                    : line.StartsWith('-')
                        ? "remove"
                        : "context";
            return new DiffLine(line, kind);
        }
    }
}
