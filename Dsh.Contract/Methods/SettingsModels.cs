using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dsh.Contract.Methods;

/// <summary>One path-addressed edit of <c>settings.mutate</c>, mirroring <c>settingsPathOpSchema</c>.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "op")]
[JsonDerivedType(typeof(SettingsPathSet), "set")]
[JsonDerivedType(typeof(SettingsPathUnset), "unset")]
public abstract record SettingsPathOp
{
    public required string[] Path { get; init; }
}

/// <summary>Set a path to a value.</summary>
public sealed record SettingsPathSet : SettingsPathOp
{
    public object? Value { get; init; }
}

/// <summary>Unset a path.</summary>
public sealed record SettingsPathUnset : SettingsPathOp
{
}

/// <summary>Helper factories.</summary>
public static class SettingsOps
{
    public static SettingsPathSet Set(string[] path, object? value) =>
        new() { Path = path, Value = value };
}

/// <summary>One redacted secret slot inside a namespace value, mirroring
/// <c>settingsSecretViewSchema</c> (path + set; the value never rides the wire).</summary>
public sealed record SettingsSecretView
{
    [JsonPropertyName("path")]
    public required string[] Path { get; init; }

    [JsonPropertyName("set")]
    public required bool Set { get; init; }
}

/// <summary>One settings namespace view, mirroring <c>settingsNamespaceViewSchema</c>.</summary>
public sealed record SettingsNamespaceView
{
    [JsonPropertyName("ns")]
    public required string Ns { get; init; }

    [JsonPropertyName("schema")]
    public JsonElement? Schema { get; init; }

    [JsonPropertyName("value")]
    public JsonElement? Value { get; init; }

    [JsonPropertyName("base")]
    public JsonElement? Base { get; init; }

    [JsonPropertyName("user")]
    public JsonElement? User { get; init; }

    [JsonPropertyName("applies")]
    public required string Applies { get; init; }

    [JsonPropertyName("secrets")]
    public SettingsSecretView[] Secrets { get; init; } = [];

    [JsonPropertyName("revision")]
    public required long Revision { get; init; }
}

/// <summary>Value of <c>settings.describe</c>, mirroring <c>settingsDescribeValueSchema</c>.</summary>
public sealed record SettingsDescribeResult
{
    [JsonPropertyName("writable")]
    public required bool Writable { get; init; }

    [JsonPropertyName("hasDocument")]
    public required bool HasDocument { get; init; }

    [JsonPropertyName("namespaces")]
    public required SettingsNamespaceView[] Namespaces { get; init; }
}

/// <summary>Value of <c>settings.update</c>/<c>mutate</c>/<c>replace</c>: the redacted namespace.</summary>
public sealed record SettingsWriteResult
{
    [JsonPropertyName("ns")]
    public required string Ns { get; init; }

    [JsonPropertyName("value")]
    public JsonElement? Value { get; init; }

    [JsonPropertyName("applies")]
    public required string Applies { get; init; }

    [JsonPropertyName("secrets")]
    public SettingsSecretView[] Secrets { get; init; } = [];

    [JsonPropertyName("revision")]
    public required long Revision { get; init; }
}
