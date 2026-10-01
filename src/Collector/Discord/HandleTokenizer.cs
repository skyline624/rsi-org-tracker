using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Collector.Discord;

/// <summary>
/// Turns Discord names into candidate RSI handles (spec § 10.1): bracketed segments such as
/// corpo tags are dropped, the rest is split on every character outside the RSI handle
/// alphabet [A-Za-z0-9_-], tokens of 3 to 60 characters are kept, and the whole name
/// (brackets dropped) without whitespace, emoji or invisible characters is added, so
/// "Pilote 42" also proposes "Pilote42". Candidates are distinct regardless of case, first
/// spelling kept: handles are compared case-insensitively.
/// </summary>
public static class HandleTokenizer
{
    public const int MinLength = 3;
    public const int MaxLength = 60;

    // Non-nested segments between [], (), {} and «»: corpo tags, "(afk)"…
    private static readonly Regex BracketedSegments = new(
        @"\[[^\]]*\]|\([^)]*\)|\{[^}]*\}|«[^»]*»",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Anything outside the RSI handle alphabet separates two tokens.
    private static readonly Regex Separators = new(@"[^A-Za-z0-9_-]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Candidate handles of one Discord name, in order: its tokens, then the whole name.</summary>
    public static IReadOnlyList<string> Tokens(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];

        var stripped = BracketedSegments.Replace(value, " ");
        var tokens = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in Separators.Split(stripped))
        {
            if (HasCandidateLength(token) && seen.Add(token)) tokens.Add(token);
        }

        var whole = WithoutSpacesOrEmoji(stripped);
        if (HasCandidateLength(whole) && seen.Add(whole)) tokens.Add(whole);
        return tokens;
    }

    /// <summary>Candidates of a member: the nick's, then the global name's, then the username's, without duplicates.</summary>
    public static IReadOnlyList<string> Candidates(string? nick, string? globalName, string username)
    {
        var candidates = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { nick, globalName, username })
        {
            foreach (var token in Tokens(name))
            {
                if (seen.Add(token)) candidates.Add(token);
            }
        }
        return candidates;
    }

    private static bool HasCandidateLength(string value) => value.Length is >= MinLength and <= MaxLength;

    private static string WithoutSpacesOrEmoji(string value)
    {
        var kept = new StringBuilder(value.Length);
        foreach (var rune in value.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune) || IsEmojiOrInvisible(rune)) continue;
            kept.Append(rune.ToString());
        }
        return kept.ToString();
    }

    /// <summary>
    /// Pictographs, flags and skin tones (So, Sk), zero-width and bidi characters, ZWJ and tags
    /// (Cf), controls, private use, keycaps (Me) and variation selectors.
    /// </summary>
    private static bool IsEmojiOrInvisible(Rune rune)
    {
        if (rune.Value is >= 0xFE00 and <= 0xFE0F or >= 0xE0100 and <= 0xE01EF) return true;
        return Rune.GetUnicodeCategory(rune) is UnicodeCategory.OtherSymbol or UnicodeCategory.ModifierSymbol
            or UnicodeCategory.Format or UnicodeCategory.Control or UnicodeCategory.PrivateUse
            or UnicodeCategory.EnclosingMark or UnicodeCategory.Surrogate;
    }
}
