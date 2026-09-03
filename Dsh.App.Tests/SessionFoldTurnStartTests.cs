using System.Text.Json;
using Dsh.App;
using Xunit;

namespace Dsh.App.Tests;

/// <summary>
/// Regression tests for the wire shape of <c>turn/start</c>. The host's payload field has
/// drifted across releases — older payloads used <c>{"turn":&lt;int&gt;}</c>, newer ones
/// sometimes ship <c>{"turnId":&lt;int&gt;}</c> / <c>{"id":&lt;int&gt;}</c>, and on rare
/// builds the field is missing entirely. The fold MUST emit a separator row in every case so
/// the WPF surface groups conversation by turn the way the Web UI does.
/// </summary>
public class SessionFoldTurnStartTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [Fact]
    public void Turn_start_accepts_legacy_turn_field()
    {
        var fold = new SessionFold();
        var ev = JsonDocument.Parse("""
{"type":"turn/start","seq":0,"data":{"turn":7}}
""").RootElement;

        Assert.True(fold.Fold(ev));
        Assert.Contains(fold.Rows, r => r.Role == "turn" && r.Text.Contains("7"));
    }

    [Fact]
    public void Turn_start_accepts_turnId_field()
    {
        var fold = new SessionFold();
        var ev = JsonDocument.Parse("""
{"type":"turn/start","seq":0,"data":{"turnId":3}}
""").RootElement;

        Assert.True(fold.Fold(ev));
        Assert.Contains(fold.Rows, r => r.Role == "turn" && r.Text.Contains("3"));
    }

    [Fact]
    public void Turn_start_accepts_id_field()
    {
        var fold = new SessionFold();
        var ev = JsonDocument.Parse("""
{"type":"turn/start","seq":0,"data":{"id":12}}
""").RootElement;

        Assert.True(fold.Fold(ev));
        Assert.Contains(fold.Rows, r => r.Role == "turn" && r.Text.Contains("12"));
    }

    [Fact]
    public void Turn_start_without_field_falls_back_to_next_turn_index()
    {
        var fold = new SessionFold();
        // Seed two prior turn rows so the fallback must walk past them.
        Assert.True(fold.Fold(JsonDocument.Parse("""
{"type":"turn/start","seq":0,"data":{"turn":2}}
""").RootElement));

        // Now a fresh turn/start with no number field at all — must still produce a row.
        Assert.True(fold.Fold(JsonDocument.Parse("""
{"type":"turn/start","seq":1,"data":{}}
""").RootElement));

        var turnRows = fold.Rows.FindAll(r => r.Role == "turn");
        Assert.Equal(2, turnRows.Count);
        Assert.Contains(turnRows, r => r.Text.Contains("2"));
        Assert.Contains(turnRows, r => r.Text.Contains("3"));
    }
}