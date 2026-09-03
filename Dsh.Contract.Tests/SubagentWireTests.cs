using System.Text.Json;
using Dsh.Contract.Methods;
using Xunit;

namespace Dsh.Contract.Tests;

/// <summary>
/// Wire-format tests for the subagent domain (subagent.list/history), written per the
/// OS rule "API development must be test-verified before main-program calls": these must
/// pass before any subagent call is wired into the UI.
/// </summary>
public class SubagentWireTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private const string CatalogJson =
        "{\"entries\":[" +
        "{\"kind\":\"child\",\"id\":\"session-child-1\",\"activity\":\"running\",\"hasChildren\":true," +
        "\"mode\":\"continuable\",\"label\":\"CodeReview\"}," +
        "{\"kind\":\"diagnostic\",\"id\":\"session-child-2\",\"reason\":\"unsupported-mode\"}" +
        "],\"parentAvailable\":true}";

    private const string HistoryJson =
        "{\"events\":[" +
        "{\"type\":\"assistant/message\",\"turn\":1,\"step\":1,\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"reviewed\"}]}}," +
        "{\"type\":\"tool/call\",\"turn\":1,\"step\":1,\"callId\":\"c1\",\"name\":\"bash\",\"arguments\":\"\"}" +
        "],\"hasMore\":false,\"projections\":{\"asOfSeq\":3,\"values\":{\"title\":\"review\"}}}";

    [Fact]
    public void SubagentCatalog_deserializes_child_and_diagnostic()
    {
        var catalog = JsonSerializer.Deserialize<SubagentCatalog>(CatalogJson, Options);

        Assert.NotNull(catalog);
        Assert.True(catalog.ParentAvailable);
        Assert.Equal(2, catalog.Entries.Length);

        var child = Assert.IsType<SubagentChild>(catalog.Entries[0]);
        Assert.Equal("session-child-1", child.Id);
        Assert.True(child.HasChildren);
        Assert.Equal("continuable", child.Mode);

        var diagnostic = Assert.IsType<SubagentDiagnostic>(catalog.Entries[1]);
        Assert.Equal("unsupported-mode", diagnostic.Reason);
    }

    [Fact]
    public void SubagentHistory_deserializes_events_and_hasMore()
    {
        var history = JsonSerializer.Deserialize<SubagentHistory>(HistoryJson, Options);

        Assert.NotNull(history);
        Assert.False(history.HasMore);
        Assert.Equal(2, history.Events.Length);

        // Each event materializes as JsonElement; verify the first is an assistant message.
        var first = Assert.IsType<JsonElement>(history.Events[0]);
        Assert.Equal("assistant/message", first.GetProperty("type").GetString());
    }

    [Fact]
    public void SubagentInterruptReceipt_deserializes_accepted_true()
    {
        const string json = "{\"accepted\":true}";
        var receipt = JsonSerializer.Deserialize<SubagentInterruptReceipt>(json, Options);
        Assert.NotNull(receipt);
        Assert.True(receipt.Accepted);
    }

    [Fact]
    public void SubagentPromptReceipt_deserializes_messageId()
    {
        const string json = "{\"messageId\":\"msg-1\"}";
        var receipt = JsonSerializer.Deserialize<SubagentPromptReceipt>(json, Options);
        Assert.NotNull(receipt);
        Assert.Equal("msg-1", receipt.MessageId);
    }
}
