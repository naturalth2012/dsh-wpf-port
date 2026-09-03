using System.Text.Json;
using Dsh.Contract.Methods;
using Dsh.Contract.Rpc;
using Xunit;

namespace Dsh.Contract.Tests;

/// <summary>
/// Real-wire-format tests: replay actual JSON bytes the host emits AND serialize the
/// outbound shapes the host must accept, asserting every property round-trips. These
/// are the regression gate that catches STJ binding bugs before they reach the GUI.
/// </summary>
public class WireFormatTests
{
    /// <summary>Serializer options mirroring the production client codec.</summary>
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Success: host returned this for <c>host.describe</c> on 2026-08-15.</summary>
    private const string HostDescribeResponse =
        "{\"type\":\"server-response\",\"rpcId\":\"probe-1\"," +
        "\"result\":{\"ok\":true,\"value\":{\"version\":\"0.0.1\"," +
        "\"cwd\":\"C:\\\\fixtures\\\\deepseek-harness\",\"provider\":\"tencent-maas\"," +
        "\"model\":\"qwen3.5-plus\",\"attachedSessions\":6,\"canOpenPath\":true}}}";

    /// <summary>Error slot shape: <c>{ ok:false, error:{ code, message, details } }</c>.</summary>
    private const string ErrorSlotResponse =
        "{\"type\":\"server-response\",\"rpcId\":\"err-probe\"," +
        "\"result\":{\"ok\":false,\"error\":{\"code\":\"bad-request\",\"message\":\"invalid payload\"," +
        "\"details\":{\"issues\":[\"missing field methodId\"]}}}}";

    /// <summary>Real <c>session.list</c> response truncated to two entries; full envelope
    /// captured via <c>Invoke-WebRequest</c> on 2026-08-15. Exposes the shape
    /// <c>{ items: SessionSummary[] }</c> that STJ must bind.</summary>
    private const string SessionListResponse =
        "{\"type\":\"server-response\",\"rpcId\":\"list-1\"," +
        "\"result\":{\"ok\":true,\"value\":{" +
        "\"items\":[" +
        "{\"sessionId\":\"session-dc58e495-d639-4a7b-9762-7afa0fd58040\"," +
        "\"updatedAt\":1786778389567,\"running\":false,\"blank\":true," +
        "\"cwd\":\"C:\\\\fixtures\\\\eastern-wisdom-archive\",\"agentPreset\":\"standard\"," +
        "\"projections\":{\"asOfSeq\":6,\"values\":{\"title\":null}}},{" +
        "\"sessionId\":\"session-f889d7d0-660b-41bd-ab80-48b0ee41a8c0\"," +
        "\"updatedAt\":1786778336515,\"running\":false,\"blank\":true," +
        "\"cwd\":\"C:\\\\fixtures\\\\xx\",\"agentPreset\":\"standard\"," +
        "\"projections\":{\"asOfSeq\":2,\"values\":{\"title\":null}}}" +
        "]}}}";

    [Fact]
    public void ServerResponse_deserializes_real_wire_json()
    {
        var response = JsonSerializer.Deserialize<ServerResponse>(HostDescribeResponse, Options);

        Assert.NotNull(response);
        Assert.Equal(RpcMessageType.ServerResponse, response.Type);
        Assert.Equal("probe-1", response.RpcId.Value);
        Assert.NotNull(response.Result);

        var el = (JsonElement)response.Result!;
        Assert.True(el.GetProperty("ok").GetBoolean());
        Assert.Equal("0.0.1", el.GetProperty("value").GetProperty("version").GetString());
    }

    [Fact]
    public void RpcId_deserializes_from_wire_string()
    {
        const string json = "\"abc-123\"";
        var id = JsonSerializer.Deserialize<RpcId>(json);
        Assert.NotNull(id);
        Assert.Equal("abc-123", id.Value);
    }

    [Fact]
    public void RpcError_deserializes_from_error_envelope_field()
    {
        var response = JsonSerializer.Deserialize<ServerResponse>(ErrorSlotResponse, Options)!;
        var result = (JsonElement)response.Result!;
        var error = result.GetProperty("error").Deserialize<RpcError>(Options);

        Assert.NotNull(error);
        Assert.Equal(RpcErrorCode.BadRequest, error.Code);
        Assert.Equal("invalid payload", error.Message);
        Assert.NotNull(error.Details);
    }

    [Fact]
    public void RpcError_rejects_top_level_deserialize()
    {
        // The whole result slot is NOT a RpcError (it's {ok:false, error:{...}}). STJ
        // must throw `missing required properties code, message` to catch the mis-binding.
        var response = JsonSerializer.Deserialize<ServerResponse>(ErrorSlotResponse, Options)!;
        var result = (JsonElement)response.Result!;

        Assert.Throws<JsonException>(() => result.Deserialize<RpcError>(Options));
    }

    [Fact]
    public void ClientRequest_serializes_to_wire_shape_host_accepts()
    {
        // The host's `clientRequestSchema` rejects envelopes whose `type` literal
        // differs from "client-request" (returning "invalid client-request message").
        // STJ must emit the wire literal, not the C# enum name.
        var req = new ClientRequest
        {
            RpcId = RpcId.Of("probe-1"),
            Method = "host.describe",
            Payload = new { },
        };

        string wire = JsonSerializer.Serialize(req, Options);

        Assert.Contains("\"type\":\"client-request\"", wire);
        Assert.Contains("\"rpcId\":\"probe-1\"", wire);
        Assert.Contains("\"method\":\"host.describe\"", wire);
    }

    [Fact]
    public void ServerResponse_round_trips_through_wire()
    {
        var req = new ClientRequest
        {
            RpcId = RpcId.Of("probe-1"),
            Method = "host.describe",
            Payload = new { },
        };

        string wire = JsonSerializer.Serialize(req, Options);
        Assert.Contains("\"type\":\"client-request\"", wire);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void HostDescribe_payload_rejects_non_object_shape(object? badPayload)
    {
        // The host's hostDescribeRequestSchema is `z.object({})`: a null/empty/non-object
        // payload fails with "invalid payload for host.describe". STJ serializes null
        // as `"payload":null` and string as `"payload":""` — both rejected by Zod's
        // `z.object({})`. Surface this as a regression guard so the GUI never ships
        // a bad payload again.
        var req = new ClientRequest
        {
            RpcId = RpcId.Of("p"),
            Method = "host.describe",
            Payload = badPayload,
        };
        string wire = JsonSerializer.Serialize(req, Options);

        Assert.DoesNotContain("\"payload\":{}", wire);
    }

    [Fact]
    public void SessionPrompt_content_parts_serialize_to_discriminated_union()
    {
        // The host's sessionPromptRequestSchema requires `content` to be a
        // discriminated union array: [{type:'text',text}, {type:'image',mediaType,data}].
        // Text-only prompts must NOT send a bare string.
        var parts = new[]
        {
            PromptParts.Text("hello"),
            PromptParts.Image("image/png", "aGVsbG8=", "x.png"),
        };

        string wire = JsonSerializer.Serialize(parts, Options);

        Assert.Contains("\"type\":\"text\"", wire);
        Assert.Contains("\"text\":\"hello\"", wire);
        Assert.Contains("\"type\":\"image\"", wire);
        Assert.Contains("\"mediaType\":\"image/png\"", wire);
        Assert.Contains("\"data\":\"aGVsbG8=\"", wire);
    }

    [Fact]
    public void SessionPrompt_payload_uses_queue_mode_and_content_array()
    {
        var req = new ClientRequest
        {
            RpcId = RpcId.Of("p"),
            Method = "session.prompt",
            Payload = new
            {
                sessionId = "s1",
                mode = "queue",
                content = new object[] { new { type = "text", text = "hi" } },
            },
        };

        string wire = JsonSerializer.Serialize(req, Options);

        Assert.Contains("\"mode\":\"queue\"", wire);
        Assert.Contains("\"content\":[{", wire);
        Assert.Contains("\"type\":\"text\"", wire);
    }

    [Fact]
    public void HostDescribe_payload_accepts_empty_object_literal()
    {
        // The only payload shape host.describe accepts is the empty object literal `{}`.
        var req = new ClientRequest
        {
            RpcId = RpcId.Of("p"),
            Method = "host.describe",
            Payload = new { },
        };
        string wire = JsonSerializer.Serialize(req, Options);

        Assert.Contains("\"payload\":{}", wire);
    }

    [Fact]
    public void SessionHistoryPage_deserializes_history_entries()
    {
        // The host's sessionHistoryValueSchema yields `{ events: HistoryEntry[], hasMore }`,
        // where each HistoryEntry is `{ event: SessionEvent, view? }`. Folding must read the
        // nested `event`, not the entry envelope.
        const string json =
            "{\"events\":[" +
            "{\"event\":{\"type\":\"assistant/message\",\"seq\":1,\"time\":1,\"data\":{\"content\":[]}}," +
            "\"view\":{\"for\":\"call\",\"view\":{\"card\":\"bash\"}}}," +
            "{\"event\":{\"type\":\"user/message\",\"seq\":2,\"time\":2,\"data\":{\"content\":[]}}}" +
            "],\"hasMore\":false,\"projections\":{\"asOfSeq\":2,\"values\":{\"title\":null}}}";

        var page = JsonSerializer.Deserialize<SessionHistoryPage>(json, Options);

        Assert.NotNull(page);
        Assert.False(page.HasMore);
        Assert.Equal(2, page.Events.Length);
        Assert.Equal("assistant/message", page.Events[0].Event.GetProperty("type").GetString());
        Assert.True(page.Events[0].View.HasValue);
        Assert.Equal("user/message", page.Events[1].Event.GetProperty("type").GetString());
    }

    [Fact]
    public void SessionHistoryPayload_matches_host_request_schema()
    {
        var req = new ClientRequest
        {
            RpcId = RpcId.Of("p"),
            Method = "session.history",
            Payload = new { sessionId = "s1", maxMessages = 40 },
        };

        string wire = JsonSerializer.Serialize(req, Options);

        Assert.Contains("\"sessionId\":\"s1\"", wire);
        Assert.Contains("\"maxMessages\":40", wire);
    }

    [Fact]
    public void WpfApiClient_payload_fallback_substitutes_null_with_empty_object()
    {
        // Every host schema is `z.object({...})`; a null payload would parse as
        // `"payload":null` and fail Zod with "invalid payload for <method>". The client
        // codec substitutes an empty object literal at serialization time.
        var req = new ClientRequest
        {
            RpcId = RpcId.Of("p"),
            Method = "session.list",
            Payload = null,
        };

        // Simulate the WpfApiClient.Call substitution before serializing.
        req = req with { Payload = req.Payload ?? new { } };
        string wire = JsonSerializer.Serialize(req, Options);

        Assert.Contains("\"payload\":{}", wire);
        Assert.DoesNotContain("\"payload\":null", wire);
    }

    [Fact]
    public void WpfApiClient_preserves_non_null_payload()
    {
        var req = new ClientRequest
        {
            RpcId = RpcId.Of("p"),
            Method = "session.prompt",
            Payload = new { sessionId = "s1", content = "hi", mode = "text" },
        };

        string wire = JsonSerializer.Serialize(req, Options);

        Assert.Contains("\"sessionId\":\"s1\"", wire);
        Assert.Contains("\"content\":\"hi\"", wire);
        Assert.Contains("\"mode\":\"text\"", wire);
    }

    [Fact]
    public void SessionListResponse_deserializes_real_wire_json()
    {
        // Drive the exact path WpfApiClient.Call<SessionListResponse> takes: extract
        // `value` (JsonElement) and deserialize to the typed record. STJ must bind the
        // wire `{ items: SessionSummary[] }` shape; any drift in DTO field names breaks.
        var response = JsonSerializer.Deserialize<ServerResponse>(SessionListResponse, Options)!;
        var value = ((JsonElement)response.Result!).GetProperty("value");

        var list = value.Deserialize<SessionListResponse>(Options);

        Assert.NotNull(list);
        Assert.Equal(2, list.Items.Length);
        Assert.Equal("session-dc58e495-d639-4a7b-9762-7afa0fd58040", list.Items[0].SessionId);
        Assert.True(list.Items[0].Blank);
        Assert.Equal("standard", list.Items[0].AgentPreset);
    }

    [Fact]
    public void SessionListResult_rejects_weak_object_array_target()
    {
        // STJ cannot deserialize a JSON object ({items:[...]}) into `object[]`; the
        // strong-typed SessionListResponse is mandatory. This guard surfaces the binding
        // requirement and prevents regressions to the `object[]` mistake.
        var response = JsonSerializer.Deserialize<ServerResponse>(SessionListResponse, Options)!;
        var value = ((JsonElement)response.Result!).GetProperty("value");

        Assert.Throws<JsonException>(() => value.Deserialize<object[]>(Options));
    }

    [Fact]
    public void WorkspaceListResponse_deserializes_real_wire_json()
    {
        // workspace.list → { items: WorkspaceView[], archivedSessionIds }.
        const string json =
            "{\"items\":[" +
            "{\"workspaceId\":\"ws-1\",\"path\":\"D:\\\\proj\",\"title\":\"proj\"," +
            "\"sessionIds\":[\"s1\",\"s2\"],\"createdAt\":\"2026-01-01T00:00:00Z\",\"updatedAt\":\"2026-01-02T00:00:00Z\"}" +
            "],\"archivedSessionIds\":[]}";

        var resp = JsonSerializer.Deserialize<WorkspaceListResponse>(json, Options);

        Assert.NotNull(resp);
        var ws = Assert.Single(resp.Items);
        Assert.Equal("ws-1", ws.WorkspaceId);
        Assert.Equal("D:\\proj", ws.Path);
        Assert.Equal(2, ws.SessionIds.Length);
        Assert.Empty(resp.ArchivedSessionIds);
    }

    [Fact]
    public void SettingsDescribe_deserializes_namespace_view()
    {
        // settings.describe → { writable, hasDocument, namespaces:[{ ns, schema, value, applies, secrets, revision }] }.
        const string json =
            "{\"writable\":true,\"hasDocument\":true,\"namespaces\":[" +
            "{\"ns\":\"ui\",\"schema\":{\"type\":\"object\"},\"value\":{\"language\":\"zh\"}," +
            "\"applies\":\"live\",\"secrets\":[],\"revision\":3}," +
            "{\"ns\":\"llm\",\"schema\":{\"type\":\"object\"},\"value\":{\"provider\":\"deepseek\"}," +
            "\"applies\":\"restart\",\"secrets\":[{\"path\":[\"apiKey\"],\"set\":false}],\"revision\":1}" +
            "]}";

        var result = JsonSerializer.Deserialize<SettingsDescribeResult>(json, Options);

        Assert.NotNull(result);
        Assert.True(result.Writable);
        Assert.True(result.HasDocument);
        Assert.Equal(2, result.Namespaces.Length);

        Assert.Equal("ui", result.Namespaces[0].Ns);
        Assert.Equal("live", result.Namespaces[0].Applies);
        Assert.Equal(3, result.Namespaces[0].Revision);

        Assert.Equal("llm", result.Namespaces[1].Ns);
        Assert.Single(result.Namespaces[1].Secrets);
        Assert.Equal(new[] { "apiKey" }, result.Namespaces[1].Secrets[0].Path);
        Assert.False(result.Namespaces[1].Secrets[0].Set);
    }

    [Fact]
    public void SettingsUpdatePayload_matches_host_request_schema()
    {
        var req = new ClientRequest
        {
            RpcId = RpcId.Of("p"),
            Method = "settings.update",
            Payload = new { ns = "ui", patch = new { language = "en" }, expectedRevision = 3 },
        };

        string wire = JsonSerializer.Serialize(req, Options);

        Assert.Contains("\"ns\":\"ui\"", wire);
        Assert.Contains("\"expectedRevision\":3", wire);
        Assert.Contains("\"language\":\"en\"", wire);
    }

    [Fact]
    public void CredentialsDescribe_deserializes_credential_views()
    {
        // credentials.describe → { credentials: { [ref]: { configured, source, writable } } }.
        const string json =
            "{\"credentials\":{" +
            "\"openai\":{\"configured\":true,\"source\":\"env\",\"writable\":false}," +
            "\"deepseek\":{\"configured\":false,\"writable\":true}" +
            "}}";

        var result = JsonSerializer.Deserialize<CredentialsDescribeResult>(json, Options);

        Assert.NotNull(result);
        Assert.Equal(2, result.Credentials.Count);

        var openai = result.Credentials["openai"];
        Assert.True(openai.Configured);
        Assert.Equal("env", openai.Source);
        Assert.False(openai.Writable);

        var deepseek = result.Credentials["deepseek"];
        Assert.False(deepseek.Configured);
        Assert.True(deepseek.Writable);
    }

    [Fact]
    public void CredentialsSetPayload_matches_host_request_schema()
    {
        // The host's credentialsSetRequestSchema is { ref, value }; `ref` is a C# keyword,
        // so build the payload with explicit key names via a dictionary.
        var req = new ClientRequest
        {
            RpcId = RpcId.Of("p"),
            Method = "credentials.set",
            Payload = new Dictionary<string, object> { ["ref"] = "deepseek", ["value"] = "sk-xxx" },
        };

        string wire = JsonSerializer.Serialize(req, Options);

        Assert.Contains("\"ref\":\"deepseek\"", wire);
        Assert.Contains("\"value\":\"sk-xxx\"", wire);
    }

    [Fact]
    public void SettingsMutatePayload_matches_host_request_schema()
    {
        // settings.mutate → { ns, ops:[{op:'set',path:[...],value} | {op:'unset',path:[...]}], expectedRevision? }.
        var req = new ClientRequest
        {
            RpcId = RpcId.Of("p"),
            Method = "settings.mutate",
            Payload = new
            {
                ns = "ui",
                ops = new object[]
                {
                    new { op = "set", path = new[] { "language" }, value = "en" },
                    new { op = "unset", path = new[] { "deprecated" } },
                },
                expectedRevision = 3,
            },
        };

        string wire = JsonSerializer.Serialize(req, Options);

        Assert.Contains("\"op\":\"set\"", wire);
        Assert.Contains("\"path\":[\"language\"]", wire);
        Assert.Contains("\"op\":\"unset\"", wire);
        Assert.Contains("\"expectedRevision\":3", wire);
    }

    [Fact]
    public void SettingsMutateValue_is_namespace_view()
    {
        const string json =
            "{\"ns\":\"ui\",\"value\":{\"language\":\"en\"},\"applies\":\"live\"," +
            "\"secrets\":[],\"revision\":4}";

        var result = JsonSerializer.Deserialize<SettingsWriteResult>(json, Options);

        Assert.NotNull(result);
        Assert.Equal("ui", result.Ns);
        Assert.Equal("live", result.Applies);
        Assert.Equal(4, result.Revision);
    }

    [Fact]
    public void DiscoverModels_deserializes_model_views()
    {
        // llm.discoverModels → { models:[{id,name,contextWindow,maxTokens}] }.
        const string json =
            "{\"models\":[" +
            "{\"id\":\"deepseek-chat\",\"name\":\"DeepSeek Chat\",\"contextWindow\":65536,\"maxTokens\":8192}," +
            "{\"id\":\"deepseek-reasoner\",\"contextWindow\":65536}" +
            "]}";

        var result = JsonSerializer.Deserialize<DiscoverModelsResult>(json, Options);

        Assert.NotNull(result);
        Assert.Equal(2, result.Models.Length);
        Assert.Equal("deepseek-chat", result.Models[0].Id);
        Assert.Equal("DeepSeek Chat", result.Models[0].Name);
        Assert.Equal(65536, result.Models[0].ContextWindow);
        Assert.Null(result.Models[1].Name);
    }

    [Fact]
    public void DiscoverModelsPayload_matches_host_request_schema()
    {
        var req = new ClientRequest
        {
            RpcId = RpcId.Of("p"),
            Method = "llm.discoverModels",
            Payload = new { settingsNs = "llm", provider = "deepseek", baseURL = "https://api.deepseek.com" },
        };

        string wire = JsonSerializer.Serialize(req, Options);

        Assert.Contains("\"settingsNs\":\"llm\"", wire);
        Assert.Contains("\"provider\":\"deepseek\"", wire);
        Assert.Contains("\"baseURL\":\"https://api.deepseek.com\"", wire);
    }

    [Fact]
    public void OpenPath_deserializes_opened_result()
    {
        // host.openPath → { opened: true }.
        const string json = "{\"opened\":true}";

        var result = JsonSerializer.Deserialize<OpenPathResult>(json, Options);

        Assert.NotNull(result);
        Assert.True(result.Opened);
    }

    [Fact]
    public void OpenPath_payload_matches_host_request_schema()
    {
        var req = new ClientRequest
        {
            RpcId = RpcId.Of("p"),
            Method = "host.openPath",
            Payload = new { path = @"D:\tmp\a.txt" },
        };

        string wire = JsonSerializer.Serialize(req, Options);

        Assert.Contains("\"path\":\"D:\\\\tmp\\\\a.txt\"", wire);
    }

    [Fact]
    public void ArchiveSession_deserializes_archived_ids()
    {
        // workspace.archiveSession → { archivedSessionIds: string[] }.
        const string json = "{\"archivedSessionIds\":[\"s-1\",\"s-2\"]}";

        var result = JsonSerializer.Deserialize<ArchiveSessionResult>(json, Options);

        Assert.NotNull(result);
        Assert.Equal(new[] { "s-1", "s-2" }, result.ArchivedSessionIds);
    }

    [Fact]
    public void ArchiveSession_payload_matches_host_request_schema()
    {
        var req = new ClientRequest
        {
            RpcId = RpcId.Of("p"),
            Method = "workspace.archiveSession",
            Payload = new { sessionId = "s-1" },
        };

        string wire = JsonSerializer.Serialize(req, Options);

        Assert.Contains("\"sessionId\":\"s-1\"", wire);
    }

    [Fact]
    public void SessionCreate_payload_omits_null_fields()
    {
        // session.create accepts workspaceId or cwd (not both, and not neither).
        // The client must omit null fields — `new { workspaceId=null, cwd=null }` would
        // serialize as {"workspaceId":null,"cwd":null} and fail host validation.
        string bothNull = JsonSerializer.Serialize(new { }, Options);
        string cwdOnly = JsonSerializer.Serialize(new { cwd = @"D:\proj" }, Options);
        string wsOnly = JsonSerializer.Serialize(new { workspaceId = "ws-1" }, Options);

        Assert.Equal("{}", bothNull);
        Assert.Contains("\"cwd\":\"D:\\\\proj\"", cwdOnly);
        Assert.DoesNotContain("workspaceId", cwdOnly);
        Assert.Contains("\"workspaceId\":\"ws-1\"", wsOnly);
        Assert.DoesNotContain("cwd", wsOnly);
    }

    [Fact]
    public void SessionCreated_deserializes_value_slot()
    {
        // session.create → { sessionId, agentPreset? }.
        const string json = "{\"sessionId\":\"new-s\",\"agentPreset\":\"general\"}";

        var result = JsonSerializer.Deserialize<SessionCreated>(json, Options);

        Assert.NotNull(result);
        Assert.Equal("new-s", result.SessionId);
        Assert.Equal("general", result.AgentPreset);
    }

    [Fact]
    public void SessionSearch_deserializes_result_items()
    {
        // session.search → { items:[{sessionId,snippet}], hasMore }.
        const string json =
            "{\"items\":[" +
            "{\"sessionId\":\"s-1\",\"snippet\":\"修复了 unicode 截断\"}," +
            "{\"sessionId\":\"s-2\",\"snippet\":\"…搜索 cap 20…\"}" +
            "],\"hasMore\":true}";

        var result = JsonSerializer.Deserialize<SessionSearchResult>(json, Options);

        Assert.NotNull(result);
        Assert.True(result.HasMore);
        Assert.Equal(2, result.Items.Length);
        Assert.Equal("s-1", result.Items[0].SessionId);
        Assert.Equal("修复了 unicode 截断", result.Items[0].Snippet);
    }

    [Fact]
    public void SessionSearch_payload_matches_host_request_schema()
    {
        var req = new ClientRequest
        {
            RpcId = RpcId.Of("p"),
            Method = "session.search",
            Payload = new { query = "unicode" },
        };

        string wire = JsonSerializer.Serialize(req, Options);

        Assert.Contains("\"method\":\"session.search\"", wire);
        Assert.Contains("\"query\":\"unicode\"", wire);
    }

    [Fact]
    public void SessionFork_deserializes_child_session_id()
    {
        // session.fork → { sessionId }.
        const string json = "{\"sessionId\":\"child-abc\"}";

        var result = JsonSerializer.Deserialize<SessionForkResult>(json, Options);

        Assert.NotNull(result);
        Assert.Equal("child-abc", result.SessionId);
    }

    [Fact]
    public void SessionFork_payload_matches_host_request_schema()
    {
        var req = new ClientRequest
        {
            RpcId = RpcId.Of("p"),
            Method = "session.fork",
            Payload = new { sessionId = "s-1", atSeq = 42 },
        };

        string wire = JsonSerializer.Serialize(req, Options);

        Assert.Contains("\"method\":\"session.fork\"", wire);
        Assert.Contains("\"sessionId\":\"s-1\"", wire);
        Assert.Contains("\"atSeq\":42", wire);
    }

    [Fact]
    public void PickDirectory_deserializes_path_nullable()
    {
        // host.pickDirectory → { path: string | null }; null = user cancelled.
        var picked = JsonSerializer.Deserialize<PickDirectoryResult>("{\"path\":\"D:\\\\proj\"}", Options);
        var cancelled = JsonSerializer.Deserialize<PickDirectoryResult>("{\"path\":null}", Options);

        Assert.NotNull(picked);
        Assert.Equal("D:\\proj", picked.Path);
        Assert.NotNull(cancelled);
        Assert.Null(cancelled.Path);
    }

    [Fact]
    public void PickDirectory_payload_is_empty_object_literal()
    {
        var req = new ClientRequest
        {
            RpcId = RpcId.Of("p"),
            Method = "host.pickDirectory",
            Payload = new { },
        };

        string wire = JsonSerializer.Serialize(req, Options);

        Assert.Contains("\"method\":\"host.pickDirectory\"", wire);
        Assert.Contains("\"payload\":{}", wire);
    }

    [Fact]
    public void WorkspaceCreate_deserializes_full_value_slot()
    {
        // workspace.create → { workspace: WorkspaceView, created: boolean }.
        const string json =
            "{\"workspace\":{" +
            "\"workspaceId\":\"ws-9\",\"path\":\"D:\\\\newproj\",\"title\":\"newproj\"," +
            "\"sessionIds\":[],\"createdAt\":\"2026-01-01T00:00:00Z\",\"updatedAt\":\"2026-01-01T00:00:00Z\"}," +
            "\"created\":true}";

        var result = JsonSerializer.Deserialize<WorkspaceCreateResult>(json, Options);

        Assert.NotNull(result);
        Assert.True(result.Created);
        Assert.Equal("ws-9", result.Workspace.WorkspaceId);
        Assert.Equal("D:\\newproj", result.Workspace.Path);
    }

    [Fact]
    public void WorkspaceCreate_payload_matches_host_request_schema()
    {
        var req = new ClientRequest
        {
            RpcId = RpcId.Of("p"),
            Method = "workspace.create",
            Payload = new { path = @"D:\newproj" },
        };

        string wire = JsonSerializer.Serialize(req, Options);

        Assert.Contains("\"method\":\"workspace.create\"", wire);
        Assert.Contains("\"path\":\"D:\\\\newproj\"", wire);
    }

    [Fact]
    public void WorkspaceDelete_deserializes_deleted_literal()
    {
        // workspace.delete → { deleted: true } literal.
        const string json = "{\"deleted\":true}";

        var result = JsonSerializer.Deserialize<WorkspaceDeleteResult>(json, Options);

        Assert.NotNull(result);
        Assert.True(result.Deleted);
    }

    [Fact]
    public void WorkspaceDelete_payload_matches_host_request_schema()
    {
        var req = new ClientRequest
        {
            RpcId = RpcId.Of("p"),
            Method = "workspace.delete",
            Payload = new { workspaceId = "ws-9" },
        };

        string wire = JsonSerializer.Serialize(req, Options);

        Assert.Contains("\"method\":\"workspace.delete\"", wire);
        Assert.Contains("\"workspaceId\":\"ws-9\"", wire);
    }

    [Fact]
    public void QuestionItem_deserializes_plan_review_intent()
    {
        // question/requested frame items carry the presentation intent under 'intent';
        // a plan-review item has kind 'plan-review' and an 'approve' text (events.schema.ts).
        const string json =
            "{\"id\":\"q-1\",\"question\":\"批准此 Plan？\",\"detail\":null,\"header\":null," +
            "\"options\":[],\"multiSelect\":false," +
            "\"intent\":{\"kind\":\"plan-review\",\"approve\":\"批准执行 3 步计划\"}}";

        var item = JsonSerializer.Deserialize<QuestionItem>(json, Options);

        Assert.NotNull(item);
        Assert.NotNull(item.Intent);
        Assert.Equal("plan-review", item.Intent.Kind);
        Assert.Equal("批准执行 3 步计划", item.Intent.Approve);
    }

    [Fact]
    public void QuestionItem_deserializes_ordinary_question_without_intent()
    {
        const string json =
            "{\"id\":\"q-2\",\"question\":\"使用哪个模型？\",\"options\":[{\"label\":\"A\"},{\"label\":\"B\"}]," +
            "\"multiSelect\":false}";

        var item = JsonSerializer.Deserialize<QuestionItem>(json, Options);

        Assert.NotNull(item);
        Assert.Null(item.Intent);
        Assert.Equal(2, item.Options!.Length);
    }

    [Fact]
    public void SkillList_deserializes_skills()
    {
        // skill.list → { skills:[{name,description,whenToUse?,modelInvocable}] }.
        const string json =
            "{\"skills\":[{" +
            "\"name\":\"file-edit\",\"description\":\"Edit a file\",\"whenToUse\":\"When editing\",\"modelInvocable\":true}," +
            "{\"name\":\"shell\",\"description\":\"Run a command\",\"modelInvocable\":false}" +
            "]}";

        var result = JsonSerializer.Deserialize<SkillListResult>(json, Options);

        Assert.NotNull(result);
        Assert.Equal(2, result.Skills.Length);
        Assert.Equal("file-edit", result.Skills[0].Name);
        Assert.True(result.Skills[0].ModelInvocable);
        Assert.Equal("shell", result.Skills[1].Name);
        Assert.False(result.Skills[1].ModelInvocable);
    }

    [Fact]
    public void SkillList_payload_matches_host_request_schema()
    {
        var req = new ClientRequest
        {
            RpcId = RpcId.Of("p"),
            Method = "skill.list",
            Payload = new { sessionId = "s-1" },
        };

        string wire = JsonSerializer.Serialize(req, Options);

        Assert.Contains("\"method\":\"skill.list\"", wire);
        Assert.Contains("\"sessionId\":\"s-1\"", wire);
    }

    [Fact]
    public void AgentPresetList_deserializes_presets()
    {
        // agentPreset.list → { presets:[{id,trust,isDefault,name?}], authorable, hasDocument }.
        const string json =
            "{\"presets\":[{" +
            "\"id\":\"general\",\"trust\":\"system\",\"isDefault\":true,\"name\":\"通用\"}," +
            "{\"id\":\"user-foo\",\"trust\":\"user\",\"isDefault\":false}" +
            "],\"authorable\":true,\"hasDocument\":false}";

        var result = JsonSerializer.Deserialize<AgentPresetListResult>(json, Options);

        Assert.NotNull(result);
        Assert.True(result.Authorable);
        Assert.False(result.HasDocument);
        Assert.Equal(2, result.Presets.Length);
        Assert.Equal("system", result.Presets[0].Trust);
        Assert.True(result.Presets[0].IsDefault);
        Assert.Null(result.Presets[1].Name);
    }

    [Fact]
    public void AgentPresetList_payload_is_empty_object_literal()
    {
        var req = new ClientRequest
        {
            RpcId = RpcId.Of("p"),
            Method = "agentPreset.list",
            Payload = new { },
        };

        string wire = JsonSerializer.Serialize(req, Options);

        Assert.Contains("\"method\":\"agentPreset.list\"", wire);
        Assert.Contains("\"payload\":{}", wire);
    }

    [Fact]
    public void SessionPrompt_command_slot_deserializes()
    {
        // session.prompt → { accepted:true, command?:{kind:'success',text?} } (slash-command result).
        const string json = "{\"accepted\":true,\"command\":{\"kind\":\"success\",\"text\":\"plan off\"}}";

        var result = JsonSerializer.Deserialize<SessionPromptResult>(json, Options);

        Assert.NotNull(result);
        Assert.True(result.Accepted);
        Assert.NotNull(result.Command);
        Assert.Equal("success", result.Command.Kind);
        Assert.Equal("plan off", result.Command.Text);
    }
}