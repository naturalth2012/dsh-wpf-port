using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dsh.Contract.Rpc;

/// <summary>
/// Closed error-code union, mirroring <c>RpcErrorCode = keyof RpcErrorDetailsMap</c>
/// in rpc.ts (40 codes). The discriminant for narrowing <see cref="RpcError.Details"/>.
///
/// STJ binding: a <see cref="RpcErrorCodeJsonConverter"/> reads/writes the wire string
/// (<c>"bad-request"</c>) by honoring each member's <c>[JsonPropertyName]</c>. The default
/// <c>JsonStringEnumConverter</c> ignores property names, so we ship our own.
/// </summary>
[JsonConverter(typeof(RpcErrorCodeJsonConverter))]
public enum RpcErrorCode
{
    [JsonPropertyName("bad-request")] BadRequest,
    [JsonPropertyName("cancelled")] Cancelled,
    [JsonPropertyName("session-not-found")] SessionNotFound,
    [JsonPropertyName("model-unavailable")] ModelUnavailable,
    [JsonPropertyName("session-conflict")] SessionConflict,
    [JsonPropertyName("invalid-time-zone")] InvalidTimeZone,
    [JsonPropertyName("workspace-attach-failed")] WorkspaceAttachFailed,
    [JsonPropertyName("workspace-not-found")] WorkspaceNotFound,
    [JsonPropertyName("workspace-invalid-path")] WorkspaceInvalidPath,
    [JsonPropertyName("workspace-name-conflict")] WorkspaceNameConflict,
    [JsonPropertyName("workspace-move-invalid")] WorkspaceMoveInvalid,
    [JsonPropertyName("directory-unreadable")] DirectoryUnreadable,
    [JsonPropertyName("directory-exists")] DirectoryExists,
    [JsonPropertyName("directory-create-failed")] DirectoryCreateFailed,
    [JsonPropertyName("directory-picker-unavailable")] DirectoryPickerUnavailable,
    [JsonPropertyName("agent-preset-read-only")] AgentPresetReadOnly,
    [JsonPropertyName("agent-preset-locked")] AgentPresetLocked,
    [JsonPropertyName("agent-preset-conflict")] AgentPresetConflict,
    [JsonPropertyName("agent-preset-not-found")] AgentPresetNotFound,
    [JsonPropertyName("agent-preset-invalid")] AgentPresetInvalid,
    [JsonPropertyName("agent-busy")] AgentBusy,
    [JsonPropertyName("attachment-error")] AttachmentError,
    [JsonPropertyName("queue-item-not-found")] QueueItemNotFound,
    [JsonPropertyName("steer-unavailable")] SteerUnavailable,
    [JsonPropertyName("command-error")] CommandError,
    [JsonPropertyName("unknown-command")] UnknownCommand,
    [JsonPropertyName("settings-rejected")] SettingsRejected,
    [JsonPropertyName("settings-not-exposed")] SettingsNotExposed,
    [JsonPropertyName("settings-conflict")] SettingsConflict,
    [JsonPropertyName("credential-rejected")] CredentialRejected,
    [JsonPropertyName("model-discovery-failed")] ModelDiscoveryFailed,
    [JsonPropertyName("title-invalid")] TitleInvalid,
    [JsonPropertyName("fork-unavailable")] ForkUnavailable,
    [JsonPropertyName("subagent-parent-unavailable")] SubagentParentUnavailable,
    [JsonPropertyName("subagent-not-found")] SubagentNotFound,
    [JsonPropertyName("subagent-catalog-diagnostic")] SubagentCatalogDiagnostic,
    [JsonPropertyName("subagent-not-resumable")] SubagentNotResumable,
    [JsonPropertyName("subagent-unauthorized")] SubagentUnauthorized,
    [JsonPropertyName("subagent-delivery-unavailable")] SubagentDeliveryUnavailable,
    [JsonPropertyName("internal")] Internal,
}

/// <summary>
/// STJ converter for <see cref="RpcErrorCode"/> that honors each member's
/// <c>[JsonPropertyName]</c>, mirroring how the host wire keys enum members.
/// </summary>
public sealed class RpcErrorCodeJsonConverter : JsonConverter<RpcErrorCode>
{
    // One index build feeds both directions: wire→enum (Read) and enum→wire (Write). The old
    // Write path did GetField+GetCustomAttribute reflection (plus enum boxing via GetType())
    // on every serialization — asymmetric with Read's cached lookup and NativeAOT-hostile.
    private static readonly Dictionary<string, RpcErrorCode> _byWire = new(StringComparer.Ordinal);
    private static readonly Dictionary<RpcErrorCode, string> _toWire = new();

    static RpcErrorCodeJsonConverter()
    {
        foreach (var field in typeof(RpcErrorCode).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var attr = field.GetCustomAttribute<JsonPropertyNameAttribute>()
                ?? throw new InvalidOperationException($"RpcErrorCode.{field.Name} missing [JsonPropertyName].");
            var value = (RpcErrorCode)field.GetValue(null)!;
            _byWire[attr.Name] = value;
            _toWire[value] = attr.Name;
        }
    }

    public override RpcErrorCode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var wire = reader.GetString() ?? throw new JsonException("RpcErrorCode expected non-null string.");
        if (_byWire.TryGetValue(wire, out var code)) return code;
        throw new JsonException($"Unknown RpcErrorCode wire value: '{wire}'.");
    }

    public override void Write(Utf8JsonWriter writer, RpcErrorCode value, JsonSerializerOptions options)
    {
        if (!_toWire.TryGetValue(value, out var name))
        {
            throw new InvalidOperationException($"RpcErrorCode.{value} has no [JsonPropertyName] mapping.");
        }
        writer.WriteStringValue(name);
    }
}

/// <summary>
/// A discriminated error, mirroring <c>RpcError</c> in rpc.ts: <c>code</c> is the
/// discriminant; <c>message</c> is the seam's own text; <c>details</c> is code-specific
/// (each code maps to its own details type via <c>RpcErrorDetailsMap</c>).
/// </summary>
public sealed record RpcError
{
    public required RpcErrorCode Code { get; init; }
    public required string Message { get; init; }

    /// <summary>
    /// Code-specific details payload. The shape follows <c>RpcErrorDetailsMap</c>; an empty
    /// object (e.g. for <c>internal</c>) deserializes to an empty <see cref="System.Text.Json.Nodes.JsonObject"/>.
    /// </summary>
    public object? Details { get; init; }
}
