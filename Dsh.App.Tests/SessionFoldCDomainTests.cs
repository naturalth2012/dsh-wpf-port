using System.Text.Json;
using Dsh.App;
using Dsh.App.Services;
using Xunit;

namespace Dsh.App.Tests;

/// <summary>
/// C13–C16 regression tests: context injection disclosure, compaction checkpoints, retry
/// status, and terminal failure rows are derived from the host's session events. Pinned to
/// zh-CN because the fold rows are localized at fold time and these assertions check Chinese.
/// </summary>
[Collection("Localization")]
public class SessionFoldCDomainTests
{
    private static SessionFold Fold(params string[] eventJsons)
    {
        Localization.SetLanguage("zh-CN");
        var fold = new SessionFold();
        foreach (var json in eventJsons)
        {
            fold.Fold(JsonDocument.Parse(json).RootElement);
        }
        return fold;
    }

    [Fact]
    public void C16_Terminal_error_renders_error_row_with_code()
    {
        var fold = Fold(
            """{"type":"turn/end","seq":1,"data":{"turn":1,"reason":{"kind":"error","error":{"message":"upstream boom","code":"UNKNOWN","status":500}}}}""");

        var error = Assert.Single(fold.Rows, r => r.Role == "error");
        Assert.Contains("upstream boom", error.Text);
    }

    [Fact]
    public void C16_Auth_error_uses_sanitized_text()
    {
        var fold = Fold(
            """{"type":"turn/end","seq":1,"data":{"turn":1,"reason":{"kind":"error","error":{"message":"invalid secret credential","code":"AUTH","status":401}}}}""");

        var error = Assert.Single(fold.Rows, r => r.Role == "error");
        Assert.Contains("API key 无效", error.Text);
        Assert.DoesNotContain("invalid secret", error.Text); // never echo credential-bearing text
    }

    [Fact]
    public void C16_Non_error_turn_end_stays_turn_marker()
    {
        var fold = Fold(
            """{"type":"turn/end","seq":1,"data":{"turn":1,"reason":{"kind":"completed"}}}""");

        Assert.DoesNotContain(fold.Rows, r => r.Role == "error");
        Assert.Contains(fold.Rows, r => r.Role == "turn");
    }

    [Fact]
    public void C13_Context_injection_renders_context_row()
    {
        var fold = Fold(
            """{"type":"user/message","seq":1,"data":{"content":"AGENTS 指令","source":{"kind":"agent-instructions","changes":[{"path":"/repo/AGENTS.md"}]}}}""");

        var context = Assert.Single(fold.Rows, r => r.Role == "context");
        Assert.Contains("AGENTS.md", context.Text);
        Assert.Contains("注入", context.Text);
    }

    [Fact]
    public void C13_Plain_user_message_stays_user_row()
    {
        var fold = Fold(
            """{"type":"user/message","seq":1,"data":{"content":"hello","source":{"kind":"user"}}}""");

        Assert.Single(fold.Rows, r => r.Role == "user");
        Assert.DoesNotContain(fold.Rows, r => r.Role == "context");
    }

    [Fact]
    public void C14_Compaction_summary_renders_checkpoint_row()
    {
        var fold = Fold(
            """{"type":"compaction/summary","seq":1,"data":{"compactionId":"c1","provider":"deepseek","model":"deepseek-chat","shadowedTokenCount":1234}}""");

        var row = Assert.Single(fold.Rows, r => r.Role == "compacted");
        Assert.Contains("1234", row.Text);
        Assert.Contains("deepseek", row.Text);
    }

    [Fact]
    public void C15_Retry_renders_retry_row()
    {
        var fold = Fold(
            """{"type":"llm/retry","seq":1,"data":{"retryId":"r1","turn":1,"step":1,"provider":"deepseek","mode":"normal","policyKey":"p","retry":2,"maxRetries":3,"delayMs":500,"failure":{"message":"boom","code":"UNKNOWN"}}}""");

        var row = Assert.Single(fold.Rows, r => r.Role == "retry");
        Assert.Contains("2", row.Text);
        Assert.Contains("3", row.Text);
    }
}
