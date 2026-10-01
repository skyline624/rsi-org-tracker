using System.Globalization;

namespace Collector.Discord;

/// <summary>Text formats of the values stored in the Discord history.</summary>
public static class DiscordFormats
{
    /// <summary>
    /// ISO 8601 UTC to the second ("2026-09-30T12:00:00Z"), for event values such as a
    /// JoinedAt. Invariant culture: another calendar would change the year.
    /// </summary>
    public static string Iso(DateTime utc) =>
        (utc.Kind == DateTimeKind.Local ? utc.ToUniversalTime() : utc)
            .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
