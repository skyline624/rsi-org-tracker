using System.Text.Json;
using System.Text.RegularExpressions;

namespace Collector.Api.Extensions;

/// <summary>
/// Parses the legacy <c>OrganizationMember.RolesJson</c> column into a clean,
/// deduplicated list of role names. The column holds a JSON array but the
/// scraped values are noisy: entries can be prefixed with the column header
/// ("Roles\n\t\tFounder"), padded with tabs/newlines, and duplicated within
/// the same array.
/// </summary>
public static class RoleParser
{
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);
    private const string LegacyHeader = "Roles ";

    public static IReadOnlyList<string> Parse(string? rolesJson)
    {
        if (string.IsNullOrWhiteSpace(rolesJson)) return Array.Empty<string>();

        List<string>? raw;
        try
        {
            raw = JsonSerializer.Deserialize<List<string>>(rolesJson);
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
        if (raw is null) return Array.Empty<string>();

        // Use a HashSet for case-insensitive dedup but preserve first-seen casing
        // by tracking via a parallel list.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>(raw.Count);
        foreach (var entry in raw)
        {
            if (string.IsNullOrWhiteSpace(entry)) continue;
            var normalized = Whitespace.Replace(entry, " ").Trim();
            if (normalized.StartsWith(LegacyHeader, StringComparison.OrdinalIgnoreCase))
                normalized = normalized[LegacyHeader.Length..].Trim();
            if (normalized.Length == 0) continue;
            if (seen.Add(normalized)) result.Add(normalized);
        }
        return result;
    }
}
