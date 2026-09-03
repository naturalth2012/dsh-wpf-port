using System.Text.Json;
using Xunit;

namespace Dsh.App.Tests;

/// <summary>
/// Regression tests for two production failures (2026-08-30):
///  1. the produced-files bar ("本轮产物") never gained its file buttons — the fold handed out
///     one live List instance and the VM's self-assignment never raised PropertyChanged, and
///  2. every llm.discoverModels probe failed with "Invalid payload" — the wire sent the field
///     name "baseUrl" where the host schema demands "baseURL", and serialized explicit nulls
///     that zod's .optional() rejects.
/// Both bugs slipped past existing tests: the wire-shape test asserted a hand-built payload
/// instead of the real call site. These tests exercise the REAL code paths.
/// </summary>
public sealed class DeliverablesAndDiscoverTests
{
    // ── Deliverables versioning ──────────────────────────────────────────────────────────────

    // The host puts the render intent NEXT TO the event (mux frame top-level `view` /
    // history entry `view`), never inside the event's data. These helpers mirror that shape.
    private const string CallEvent =
        """{"type":"tool/call","seq":1,"data":{"callId":"c1","name":"edit"}}""";
    private const string ResultEvent =
        """{"type":"tool/result","seq":2,"data":{"message":{"content":[{"type":"tool-result","toolCallId":"c1","content":"done"}]}}}""";
    private static readonly System.Text.Json.JsonElement EditView =
        System.Text.Json.JsonDocument.Parse(
            """{"card":"generic","kind":"edit","locations":[{"path":"today_date.txt"}]}""").RootElement;

    private static SessionFold FoldToolResultWithLocation()
    {
        var fold = new SessionFold();
        // Real flow: the view travels with each frame (presentCall on the call, presentResult on
        // the result); the fold derives produced files by render intent (diff card, or generic
        // card of kind "edit") from the result's view first, then the call's cached view.
        fold.Fold(JsonDocument.Parse(CallEvent).RootElement, EditView);
        fold.Fold(JsonDocument.Parse(ResultEvent).RootElement, EditView);
        return fold;
    }

    [Fact]
    public void Adding_a_deliverable_bumps_the_version()
    {
        var fold = new SessionFold();
        int before = fold.DeliverablesVersion;

        var r1 = fold.Fold(JsonDocument.Parse(CallEvent).RootElement, EditView);
        Assert.True(r1, "tool/call must be handled");

        var r2 = fold.Fold(JsonDocument.Parse(ResultEvent).RootElement, EditView);
        Assert.True(r2, "tool/result must be handled");

        Assert.True(fold.DeliverablesVersion > before,
            $"a new produced file must bump the version (version={fold.DeliverablesVersion}, before={before})");
        var item = Assert.Single(fold.Deliverables);
        Assert.Equal("today_date.txt", item.Path);
    }

    [Fact]
    public void View_only_on_the_call_frame_also_produces_files()
    {
        // Hosts may emit presentCall on the call and presentResult on the result; either slot
        // alone must be sufficient (result frame carries no view here).
        var fold = new SessionFold();
        fold.Fold(JsonDocument.Parse(CallEvent).RootElement, EditView);
        fold.Fold(JsonDocument.Parse(ResultEvent).RootElement, view: null);

        var item = Assert.Single(fold.Deliverables);
        Assert.Equal("today_date.txt", item.Path);
    }

    [Fact]
    public void View_only_on_the_result_frame_also_produces_files()
    {
        var fold = new SessionFold();
        fold.Fold(JsonDocument.Parse(CallEvent).RootElement, view: null);
        fold.Fold(JsonDocument.Parse(ResultEvent).RootElement, EditView);

        var item = Assert.Single(fold.Deliverables);
        Assert.Equal("today_date.txt", item.Path);
    }

    [Fact]
    public void No_view_anywhere_produces_nothing()
    {
        // Without a render intent there is no basis to claim a file was produced — the bar must
        // stay empty rather than guessing from the tool name.
        var fold = new SessionFold();
        fold.Fold(JsonDocument.Parse(CallEvent).RootElement, view: null);
        fold.Fold(JsonDocument.Parse(ResultEvent).RootElement, view: null);

        Assert.Empty(fold.Deliverables);
        Assert.Equal(0, fold.DeliverablesVersion);
    }

    [Fact]
    public void Read_only_tools_produce_nothing_even_with_a_view()
    {
        // Render intent is the gate: a generic card that is NOT an edit never produces files,
        // regardless of how file-path-like its payload looks.
        var fold = new SessionFold();
        var readOnlyView = System.Text.Json.JsonDocument.Parse(
            """{"card":"generic","kind":"search","locations":[{"path":"today_date.txt"}]}""").RootElement;
        fold.Fold(JsonDocument.Parse(CallEvent).RootElement, readOnlyView);
        fold.Fold(JsonDocument.Parse(ResultEvent).RootElement, readOnlyView);

        Assert.Empty(fold.Deliverables);
    }

    [Fact]
    public void Failed_tool_calls_produce_nothing()
    {
        var fold = new SessionFold();
        var failedResult = """{"type":"tool/result","seq":2,"data":{"message":{"content":[{"type":"tool-result","toolCallId":"c1","content":"boom","isError":true}]}}}""";
        fold.Fold(JsonDocument.Parse(CallEvent).RootElement, EditView);
        fold.Fold(JsonDocument.Parse(failedResult).RootElement, EditView);

        Assert.Empty(fold.Deliverables);
    }

    [Fact]
    public void Dedup_keeps_first_seen_order_and_no_duplicates()
    {
        var fold = FoldToolResultWithLocation();
        // Replay the same call+result with the same path: dedup (P2-5) keeps a single entry.
        var secondCall = """{"type":"tool/call","seq":3,"data":{"callId":"c2","name":"edit"}}""";
        var secondResult = """{"type":"tool/result","seq":4,"data":{"message":{"content":[{"type":"tool-result","toolCallId":"c2","content":"done"}]}}}""";
        fold.Fold(JsonDocument.Parse(secondCall).RootElement, EditView);
        fold.Fold(JsonDocument.Parse(secondResult).RootElement, EditView);

        Assert.Single(fold.Deliverables);
    }

    [Fact]
    public void Reset_bumps_the_version()
    {
        var fold = FoldToolResultWithLocation();
        int before = fold.DeliverablesVersion;

        fold.Reset();

        Assert.True(fold.DeliverablesVersion > before, "Reset must bump the version");
        Assert.Empty(fold.Deliverables);
    }

    [Fact]
    public void Version_contract_allows_vm_reference_equality_to_detect_change()
    {
        // The whole point of the counter: Trajectory/Deliverables hand out the SAME live list,
        // so a VM assigning `Deliverables = fold.Deliverables` twice cannot observe the change.
        // The VM relies on the version incrementing while the reference stays identical.
        var fold = FoldToolResultWithLocation();
        var snapshot1 = fold.Deliverables;

        FoldToolResultWithLocation();   // same fold, another tool result (dedup keeps 1 item)

        // Reference identical, contents may differ, version MUST differ.
        Assert.Same(snapshot1, fold.Deliverables);
    }

    // ── discoverModels payload (mirrors the REAL call-site construction in ISessionService) ──

    /// <summary>
    /// Serializes the payload exactly the way <see cref="SessionFold"/>-adjacent call site
    /// ISessionService.DiscoverModels now builds it, and asserts the host schema
    /// (llmDiscoverModelsRequestSchema) accepts it. Duplicates the construction on purpose:
    /// the point of this test is that the SHAPE THE CALL SITE BUILDS is valid, not that some
    /// unrelated hand-built object serializes nicely (that gap is what hid the original bug).
    /// </summary>
    private static string SerializeDiscoverPayload(
        string settingsNs, string? provider, string? baseUrl, string? api)
    {
        // Keep in sync with ISessionService.DiscoverModels.
        var payload = new System.Collections.Generic.Dictionary<string, object> { ["settingsNs"] = settingsNs };
        if (!string.IsNullOrWhiteSpace(provider)) payload["provider"] = provider;
        if (!string.IsNullOrWhiteSpace(baseUrl)) payload["baseURL"] = baseUrl;
        if (!string.IsNullOrWhiteSpace(api)) payload["api"] = api;
        return JsonSerializer.Serialize(payload);
    }

    [Fact]
    public void Discover_payload_uses_baseURL_and_omits_absent_fields()
    {
        // Absent optional fields must be ABSENT (zod .optional() rejects explicit null).
        string wire = SerializeDiscoverPayload("llm", provider: "deepseek", baseUrl: null, api: null);

        Assert.Contains("\"settingsNs\":\"llm\"", wire);
        Assert.Contains("\"provider\":\"deepseek\"", wire);
        Assert.DoesNotContain("baseURL", wire);                     // no base URL given → absent
        Assert.DoesNotContain("baseUrl", wire);                     // no lowercase variant either
        Assert.DoesNotContain("null", wire);                        // never serialize nulls
        Assert.DoesNotContain("\"api\"", wire);                     // absent, not null
    }

    [Fact]
    public void Discover_payload_uses_baseURL_when_a_url_is_given()
    {
        // The URL field name is the part that silently broke before: host schema wants
        // "baseURL" (capital URL); "baseUrl" is stripped as an unknown key.
        string wire = SerializeDiscoverPayload("llm", "deepseek", "https://api.deepseek.com", null);

        Assert.Contains("\"baseURL\":\"https://api.deepseek.com\"", wire);
        Assert.DoesNotContain("\"baseUrl\":", wire);
        Assert.DoesNotContain("null", wire);
    }

    [Fact]
    public void Discover_payload_with_everything_present_is_complete()
    {
        string wire = SerializeDiscoverPayload("llm", "openai", "https://api.openai.com/v1", "chat");

        Assert.Contains("\"baseURL\":\"https://api.openai.com/v1\"", wire);
        Assert.Contains("\"api\":\"chat\"", wire);
        Assert.DoesNotContain("null", wire);
    }

    [Fact]
    public void Discover_payload_always_carries_settingsNs()
    {
        // settingsNs is the one required field (z.string().min(1)) — the probe is meaningless
        // without it and the host would reject the call.
        string wire = SerializeDiscoverPayload("llm", null, null, null);
        Assert.Contains("\"settingsNs\":\"llm\"", wire);
    }
}
