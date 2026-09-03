namespace Dsh.App;

/// <summary>
/// Samples the client timezone for prompt payloads, mirroring the Web client's
/// <c>Intl.DateTimeFormat().resolvedOptions().timeZone</c> via <c>TimeZoneInfo.Local.Id</c>.
/// The host rejects an empty/unknown zone; a resolvable IANA id is required.
/// </summary>
public static class TimeZoneSampler
{
    /// <summary>The IANA timezone id attached to <c>session.prompt</c>/<c>subagent.prompt</c> payloads.</summary>
    public static string Sample()
    {
        string id = TimeZoneInfo.Local.Id;
        // Windows returns registry ids (e.g. "China Standard Time"); map the common ones to
        // IANA ids the host recognizes, mirroring the browser's IANA output.
        return id switch
        {
            "China Standard Time" => "Asia/Shanghai",
            "Tokyo Standard Time" => "Asia/Tokyo",
            "Pacific Standard Time" => "America/Los_Angeles",
            "Mountain Standard Time" => "America/Denver",
            "Central Standard Time" => "America/Chicago",
            "Eastern Standard Time" => "America/New_York",
            "GMT Standard Time" => "Europe/London",
            "W. Europe Standard Time" => "Europe/Berlin",
            _ => id,
        };
    }
}
