using System.Text.RegularExpressions;

namespace Collector.Discord;

/// <summary>
/// Corpo tags written in Discord names (spec § 10.1): the content of a bracketed segment
/// ([], (), {}, «», 【】), and the first and last segments of a name split on "|", when shaped
/// like an RSI SID (2 to 10 of [A-Za-z0-9_-]). Candidates are upper-cased like stored SIDs and
/// distinct, in order of appearance; the caller keeps only those an organization really has,
/// so "(afk)" or "| FR" only count when such an organization exists.
/// </summary>
public static class CorpTagExtractor
{
    public const int MinLength = 2;
    public const int MaxLength = 10;

    private static readonly Regex BracketedContent = new(
        @"\[([^\]]*)\]|\(([^)]*)\)|\{([^}]*)\}|«([^»]*)»|【([^】]*)】",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SidShape = new(
        $"^[A-Za-z0-9_-]{{{MinLength},{MaxLength}}}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Tag candidates of one Discord name: bracketed contents first, then the sides of a "|".</summary>
    public static IReadOnlyList<string> Candidates(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];

        var candidates = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(string segment)
        {
            var trimmed = segment.Trim();
            if (SidShape.IsMatch(trimmed) && seen.Add(trimmed.ToUpperInvariant())) candidates.Add(trimmed.ToUpperInvariant());
        }

        foreach (Match match in BracketedContent.Matches(value))
            Add(match.Groups.Values.Skip(1).First(g => g.Success).Value);

        var sides = value.Split('|');
        if (sides.Length >= 2)
        {
            Add(sides[0]);
            Add(sides[^1]);
        }
        return candidates;
    }

    /// <summary>Tag candidates of a member: the nick's, then the global name's, without duplicates.</summary>
    public static IReadOnlyList<string> Candidates(string? nick, string? globalName)
        => Candidates(nick).Concat(Candidates(globalName)).Distinct(StringComparer.Ordinal).ToList();
}
