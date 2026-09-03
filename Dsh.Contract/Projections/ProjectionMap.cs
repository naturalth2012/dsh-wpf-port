namespace Dsh.Contract.Projections;

/// <summary>
/// Projection key registry, mirroring the keys merged into <c>SessionProjectionMap</c>
/// across the session-projection domain packages. A key absent from a projection value
/// means the capability is absent (its domain plugin is unmounted).
/// </summary>
public static class ProjectionKeys
{
    public const string Title = "title";
    public const string SessionStats = "sessionStats";
    public const string Plan = "plan";
    public const string Permissions = "permissions";
    public const string Goal = "goal";
    public const string Todos = "todos";
    public const string TokenUsage = "tokenUsage";
    public const string ContextPressure = "contextPressure";
    public const string ContextBreakdown = "contextBreakdown";
    public const string ImageLimits = "imageLimits";
    public const string SessionListMetadata = "sessionListMetadata";

    /// <summary>All 11 projection keys, in declaration order.</summary>
    public static readonly string[] All =
    {
        Title, SessionStats, Plan, Permissions, Goal, Todos,
        TokenUsage, ContextPressure, ContextBreakdown, ImageLimits, SessionListMetadata,
    };
}

/// <summary>Plan collaboration state (plan-mode): active is the logged state in force.</summary>
public sealed record PlanProjection
{
    public required bool Active { get; init; }
    public required bool Pending { get; init; }
}

/// <summary>One select-option the presentation layer advertises for a permission preset.</summary>
public sealed record PresetOption
{
    public required string Value { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
}

/// <summary>Whole permissions projection value.</summary>
public sealed record PermissionSelect
{
    public required PresetOption[] Options { get; init; }
    public required string CurrentValue { get; init; }
}

/// <summary>Whole-log conversation figures (session-stats).</summary>
public sealed record SessionStatsProjection
{
    public required int Turns { get; init; }
    public required int Steps { get; init; }
    public required long LlmMs { get; init; }
    public required long ToolMs { get; init; }
    public required long TtftMs { get; init; }
    public required int TtftSteps { get; init; }
    public required long DecodeMs { get; init; }
    public required long DecodeTokens { get; init; }
}

/// <summary>Durable cumulative provider usage for a complete session log.</summary>
public sealed record TokenUsageProjection
{
    public required long UncachedInputTokens { get; init; }
    public required long OutputTokens { get; init; }
    public required long CacheReadTokens { get; init; }
    public required long CacheWriteTokens { get; init; }
}

/// <summary>Newest request pressure paired with the newest known route capacity.</summary>
public sealed record ContextPressureProjection
{
    public long? PressureTokens { get; init; }
    public long? ProjectedTokens { get; init; }
    public long? ContextWindow { get; init; }
}

/// <summary>Heuristic system/tools/message composition of the next request.</summary>
public sealed record ContextBreakdownProjection
{
    public required long SystemTokens { get; init; }
    public required long ToolsTokens { get; init; }
    public required long MessageTokens { get; init; }
}

/// <summary>Persisted hints used to summarize a cold Session without reading a large log.</summary>
public sealed record SessionListMetadata
{
    public required bool Blank { get; init; }
    public long? LastPromptAt { get; init; }
}
