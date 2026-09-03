using System.Text.Json.Serialization;

namespace Dsh.Contract.Methods;

/// <summary>One credential's status view, mirroring <c>credentialViewSchema</c>.</summary>
public sealed record CredentialView
{
    [JsonPropertyName("configured")]
    public required bool Configured { get; init; }

    [JsonPropertyName("source")]
    public string? Source { get; init; }

    [JsonPropertyName("writable")]
    public required bool Writable { get; init; }
}

/// <summary>Value of <c>credentials.describe</c>, mirroring <c>credentialsDescribeValueSchema</c>.</summary>
public sealed record CredentialsDescribeResult
{
    [JsonPropertyName("credentials")]
    public required Dictionary<string, CredentialView> Credentials { get; init; }
}

/// <summary>Value of <c>credentials.set</c>/<c>unset</c> (both empty).</summary>
public sealed record CredentialsMutateResult;
