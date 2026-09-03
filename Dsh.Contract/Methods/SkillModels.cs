using System.Text.Json.Serialization;

namespace Dsh.Contract.Methods;

/// <summary>
/// Response of <c>skill.list</c>, mirroring <c>skillListValueSchema</c>: the catalog of
/// installable/invokable skills for a session. Each skill name doubles as a slash-command
/// candidate (<c>/&lt;name&gt;</c>) for the C12 command catalog.
/// </summary>
public sealed record SkillListResult
{
    [JsonPropertyName("skills")]
    public required SkillEntry[] Skills { get; init; }
}

/// <summary>One skill row of <c>skill.list</c>, mirroring <c>skillEntrySchema</c>.</summary>
public sealed record SkillEntry
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("description")]
    public required string Description { get; init; }

    [JsonPropertyName("whenToUse")]
    public string? WhenToUse { get; init; }

    [JsonPropertyName("modelInvocable")]
    public required bool ModelInvocable { get; init; }
}
