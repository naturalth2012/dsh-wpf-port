using System.Text.Json;
using Dsh.App;
using Xunit;

namespace Dsh.App.Tests;

/// <summary>
/// H5 (P2-4) regression tests: the fold records a messageId on assistant/user rows so a UI
/// can attach message feedback (messageFeedback.put) to the right message.
/// </summary>
public class SessionFoldMessageIdTests
{
    private static SessionFold Fold(params string[] eventJsons)
    {
        var fold = new SessionFold();
        foreach (var json in eventJsons)
        {
            fold.Fold(JsonDocument.Parse(json).RootElement);
        }
        return fold;
    }

    [Fact]
    public void Assistant_message_records_messageId()
    {
        var fold = Fold(
            """{"type":"assistant/message","seq":1,"data":{"message":{"id":"msg-1","content":[{"type":"text","text":"hi"}]}}}""");

        var assistant = Assert.Single(fold.Rows, r => r.Role == "assistant");
        Assert.Equal("msg-1", assistant.MessageId);
    }

    [Fact]
    public void User_message_records_messageId()
    {
        // Authoritative host wire (dsh-llm Message): id lives directly on the message
        // payload, not under a `message:` wrapper.
        var fold = Fold(
            """{"type":"user/message","seq":1,"data":{"id":"msg-u","role":"user","content":[{"type":"text","text":"hello"}],"source":{"kind":"user"}}}""");

        var user = Assert.Single(fold.Rows, r => r.Role == "user");
        Assert.Equal("msg-u", user.MessageId);
    }

    [Fact]
    public void Assistant_without_id_has_null_messageId()
    {
        var fold = Fold(
            """{"type":"assistant/message","seq":1,"data":{"message":{"content":[{"type":"text","text":"hi"}]}}}""");

        Assert.Null(Assert.Single(fold.Rows, r => r.Role == "assistant").MessageId);
    }
}
