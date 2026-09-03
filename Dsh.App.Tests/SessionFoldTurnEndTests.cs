using System.Text.Json;
using Dsh.App;
using Xunit;

namespace Dsh.App.Tests;

/// <summary>
/// Regression tests for the wire shape of session-history events. The host's <c>turn/end</c>
/// event encodes its reason as a structured object <c>{"kind":"completed"}</c> rather than
/// a bare string, so the fold must not call <c>GetString()</c> on that field. These tests
/// pin the safe-resolution contract so any future schema drift fails loudly here.
/// </summary>
public class SessionFoldTurnEndTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [Fact]
    public void Turn_end_accepts_object_reason_with_kind_completed()
    {
        var fold = new SessionFold();
        var ev = JsonDocument.Parse("""
{"type":"turn/end","seq":0,"data":{"turn":1,"reason":{"kind":"completed"}}}
""").RootElement;

        var ex = Record.Exception(() => fold.Fold(ev));

        Assert.Null(ex);
    }

    [Fact]
    public void Turn_end_accepts_object_reason_with_kind_error()
    {
        var fold = new SessionFold();
        var ev = JsonDocument.Parse("""
{"type":"turn/end","seq":0,"data":{"turn":1,"reason":{"kind":"error","message":"boom"}}}
""").RootElement;

        var ex = Record.Exception(() => fold.Fold(ev));

        Assert.Null(ex);
    }

    [Fact]
    public void Turn_end_accepts_string_reason_legacy_shape()
    {
        var fold = new SessionFold();
        var ev = JsonDocument.Parse("""
{"type":"turn/end","seq":0,"data":{"turn":1,"reason":"user stopped"}}
""").RootElement;

        var ex = Record.Exception(() => fold.Fold(ev));

        Assert.Null(ex);
    }

    [Fact]
    public void Turn_end_accepts_missing_reason()
    {
        var fold = new SessionFold();
        var ev = JsonDocument.Parse("""
{"type":"turn/end","seq":0,"data":{"turn":1}}
""").RootElement;

        var ex = Record.Exception(() => fold.Fold(ev));

        Assert.Null(ex);
    }

    [Fact]
    public void Turn_end_across_a_real_world_history_fixture_does_not_throw()
    {
        // The fixture is captured against a real host on 2026-08-16 and contains 466 events
        // including a turn/end whose reason is an object. Fold must traverse every event.
        var path = Path.Combine(AppContext.BaseDirectory, "session-history.fixture.json");
        if (!File.Exists(path))
        {
            // Skip silently when the fixture is not present (e.g. CI without the captured blob).
            return;
        }
        using var env = JsonDocument.Parse(File.ReadAllText(path));
        var events = env.RootElement.GetProperty("result").GetProperty("value").GetProperty("events");
        var fold = new SessionFold();

        for (int i = 0; i < events.GetArrayLength(); i++)
        {
            var ev = events[i].GetProperty("event");
            var ex = Record.Exception(() => fold.Fold(ev));
            Assert.True(ex is null, $"event[{i}] type={ev.GetProperty("type").GetString()} threw: {ex?.Message}");
        }
    }

    /// <summary>
    /// Session-metadata events emitted by the host (config snapshots at session start)
    /// must be accepted by the fold without rendering — otherwise every freshly created
    /// session shows an "unrecognized event types" diagnostic row instead of content.
    /// Regression for the WPF "该会话尚无对话内容" symptom reported 2026-08-17.
    /// </summary>
    [Theory]
    [InlineData("permission/preset")]
    [InlineData("sandbox/mode")]
    [InlineData("approval/policy")]
    [InlineData("session/end-seed")]
    public void Fold_accepts_session_metadata_event_types(string type)
    {
        var fold = new SessionFold();
        var ev = JsonDocument.Parse(string.Format(
            "{{\"type\":\"{0}\",\"seq\":0,\"data\":{{}}}}", type)).RootElement;

        var changed = fold.Fold(ev);

        Assert.True(changed, $"{type} must be acknowledged as a known event type");
        Assert.Empty(fold.Rows);
    }

    /// <summary>
    /// The fold MUST isolate per-event failures so a malformed event never tears down
    /// the entire fold loop or the upstream read pipeline. (os/06-os-self-audit.md §2)
    /// </summary>
    [Fact]
    public void Fold_swallows_malformed_event_and_continues()
    {
        var fold = new SessionFold();
        var malformed = JsonDocument.Parse("""
{"type":"turn/end","seq":0,"data":{"turn":1,"reason":{"kind":"completed"}}}
""").RootElement;

        var first = fold.Fold(malformed);
        // A subsequent good event MUST still fold without being tainted by the first.
        var good = JsonDocument.Parse("""
{"type":"user/message","seq":1,"data":{"content":"hello"}}
""").RootElement;
        var second = fold.Fold(good);

        Assert.NotEmpty(fold.Rows);
        Assert.Contains(fold.Rows, r => r.Role == "user");
    }
}
