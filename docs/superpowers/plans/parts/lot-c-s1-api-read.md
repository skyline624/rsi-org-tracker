### Task C1: Rank resolver and handle tokenizer

**Files:**
- Create: `src/Collector/Discord/DiscordRankResolver.cs`
- Create: `src/Collector/Discord/DiscordRoleLists.cs`
- Create: `src/Collector/Discord/HandleTokenizer.cs`
- Test: `src/Collector.Tests/Discord/DiscordRankResolverTests.cs`
- Test: `src/Collector.Tests/Discord/DiscordRoleListsTests.cs`
- Test: `src/Collector.Tests/Discord/HandleTokenizerTests.cs`

**Interfaces:**
- Consumes:
  - CONTRACTS § 1 (lot A): `Collector.Models.DiscordRole` (`RoleId`, `Name`, `Position`, `Color`, `IsRank`, `RankOrder`, `DeletedAt`) and the folder/namespace `src/Collector/Discord/` = `Collector.Discord`.
  - CONTRACTS § 2/§ 8 (lot A): `discord_members.RoleIdsJson` is a sorted JSON array of role id strings; `roles_changed` event values are compact JSON arrays `[{"id":"…","name":"…"}]`.
- Produces (all in namespace `Collector.Discord`):
  - `public sealed record RankRole(string RoleId, string Name, string? Color)`.
  - `DiscordRankResolver.Resolve(IEnumerable<string> memberRoleIds, IReadOnlyDictionary<string, DiscordRole> guildRoles) : RankRole?` (CONTRACTS § 7).
  - `DiscordRankResolver.Compare(DiscordRole a, DiscordRole b) : int` — negative when `a` ranks above `b`; used to sort rank lists.
  - `public sealed record EventRole(string Id, string Name)`.
  - `DiscordRoleLists.ParseRoleIds(string? roleIdsJson) : IReadOnlyList<string>` and `DiscordRoleLists.ParseEventRoles(string? eventValue) : IReadOnlyList<EventRole>` — a malformed value reads as an empty list.
  - `HandleTokenizer.Tokens(string? value) : IReadOnlyList<string>`, `HandleTokenizer.Candidates(string? nick, string? globalName, string username) : IReadOnlyList<string>` (CONTRACTS § 7), `HandleTokenizer.MinLength = 3`, `HandleTokenizer.MaxLength = 60`.
  - Decision: the whole-name candidate (spec § 10.1 step 4) is taken **after** the bracketed segments are removed, with whitespace, emoji (Unicode `So`/`Sk`), format/invisible characters (`Cf`, e.g. zero-width, bidi overrides, ZWJ), control, private-use, enclosing marks and variation selectors removed; it obeys the same 3–60 length bound.

- [ ] **Step 1: Write the failing rank resolver test**

Create `src/Collector.Tests/Discord/DiscordRankResolverTests.cs`:

```csharp
using Collector.Discord;
using Collector.Models;
using FluentAssertions;
using Xunit;

namespace Collector.Tests.Discord;

/// <summary>
/// A member's rank is deduced on read (spec § 9.5): among their live rank roles, RankOrder
/// descending, then Position descending, then RoleId (ordinal).
/// </summary>
public class DiscordRankResolverTests
{
    private static readonly DateTime Seen = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

    private static DiscordRole Role(
        string roleId, int position, int? rankOrder, bool isRank = true, bool deleted = false, string? color = "#e67e22") => new()
    {
        GuildId = "100000000000000000",
        RoleId = roleId,
        Name = $"Role {roleId}",
        Position = position,
        Color = color,
        Hoist = isRank,
        IsRank = isRank,
        RankOrder = rankOrder,
        FirstSeenAt = Seen,
        LastSeenAt = Seen,
        DeletedAt = deleted ? Seen : null,
    };

    private static IReadOnlyDictionary<string, DiscordRole> Guild(params DiscordRole[] roles)
        => roles.ToDictionary(r => r.RoleId);

    [Fact]
    public void TheHighestRankOrder_Wins_WhateverThePositions()
    {
        var roles = Guild(Role("11", position: 50, rankOrder: 1), Role("12", position: 5, rankOrder: 9));

        DiscordRankResolver.Resolve(["11", "12"], roles)!.RoleId.Should().Be("12");
    }

    [Fact]
    public void OnATieOfRankOrder_TheHigherPositionWins()
    {
        var roles = Guild(Role("11", position: 3, rankOrder: 7), Role("12", position: 8, rankOrder: 7));

        DiscordRankResolver.Resolve(["11", "12"], roles)!.RoleId.Should().Be("12");
    }

    [Fact]
    public void OnATieOfOrderAndPosition_TheRoleIdDecides_Ordinally()
    {
        var roles = Guild(Role("9", position: 4, rankOrder: 4), Role("10", position: 4, rankOrder: 4));

        // Ordinal: "10" sorts before "9".
        DiscordRankResolver.Resolve(["9", "10"], roles)!.RoleId.Should().Be("10");
    }

    [Fact]
    public void ARankWithoutOrder_ComesAfterRanksWithOne()
    {
        var roles = Guild(Role("21", position: 90, rankOrder: null), Role("22", position: 1, rankOrder: 0));

        DiscordRankResolver.Resolve(["21", "22"], roles)!.RoleId.Should().Be("22");
    }

    [Fact]
    public void DeletedRanks_AreIgnored()
    {
        var roles = Guild(Role("31", position: 9, rankOrder: 9, deleted: true), Role("32", position: 1, rankOrder: 1));

        DiscordRankResolver.Resolve(["31", "32"], roles)!.RoleId.Should().Be("32");
    }

    [Fact]
    public void RolesThatAreNotRanks_AreIgnored()
    {
        var roles = Guild(Role("41", position: 99, rankOrder: 99, isRank: false), Role("42", position: 1, rankOrder: 1));

        DiscordRankResolver.Resolve(["41", "42"], roles)!.RoleId.Should().Be("42");
    }

    [Fact]
    public void NoLiveRank_GivesNull()
    {
        var roles = Guild(Role("51", position: 5, rankOrder: 5, isRank: false), Role("52", position: 6, rankOrder: 6, deleted: true));

        DiscordRankResolver.Resolve(["51", "52", "404"], roles).Should().BeNull();
        DiscordRankResolver.Resolve([], roles).Should().BeNull();
    }

    [Fact]
    public void UnknownRoleIds_AreIgnored()
    {
        var roles = Guild(Role("61", position: 1, rankOrder: 1));

        DiscordRankResolver.Resolve(["999", "61"], roles)!.RoleId.Should().Be("61");
    }

    [Fact]
    public void TheRank_CarriesTheRolesCurrentNameAndColour()
    {
        var roles = Guild(Role("71", position: 1, rankOrder: 1, color: null));

        DiscordRankResolver.Resolve(["71"], roles).Should().Be(new RankRole("71", "Role 71", null));
    }

    [Fact]
    public void TheOrderOfTheMembersRoles_DoesNotMatter()
    {
        var roles = Guild(Role("81", position: 1, rankOrder: 1), Role("82", position: 2, rankOrder: 2), Role("83", position: 3, rankOrder: 3));

        DiscordRankResolver.Resolve(["81", "83", "82"], roles)!.RoleId.Should().Be("83");
        DiscordRankResolver.Resolve(["83", "82", "81"], roles)!.RoleId.Should().Be("83");
    }

    [Fact]
    public void Compare_SortsRanksFromTheHighest()
    {
        var ranks = new List<DiscordRole>
        {
            Role("94", position: 1, rankOrder: null),
            Role("91", position: 1, rankOrder: 5),
            Role("93", position: 9, rankOrder: 2),
            Role("92", position: 1, rankOrder: 2),
        };

        ranks.Sort(DiscordRankResolver.Compare);

        ranks.Select(r => r.RoleId).Should().Equal("91", "93", "92", "94");
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test src/Collector.Tests --filter "FullyQualifiedName~DiscordRankResolverTests"`

Expected: the build fails with `error CS0103: The name 'DiscordRankResolver' does not exist in the current context` (and `error CS0246: The type or namespace name 'RankRole' could not be found`).

- [ ] **Step 3: Write the rank resolver**

Create `src/Collector/Discord/DiscordRankResolver.cs`:

```csharp
using Collector.Models;

namespace Collector.Discord;

/// <summary>A member's rank: the Discord role that stands for it, with its current name and colour.</summary>
public sealed record RankRole(string RoleId, string Name, string? Color);

/// <summary>
/// Deduces a member's rank on read (spec § 9.5). Among the member's roles that are ranks and
/// are not deleted, the first in rank order wins: RankOrder descending, then Position
/// descending, then RoleId (ordinal). Nothing is stored, so changing the rank configuration
/// re-reads the whole history without rewriting it.
/// </summary>
public static class DiscordRankResolver
{
    /// <summary>
    /// The member's rank, or null when none of <paramref name="memberRoleIds"/> is a live rank
    /// role of the guild. Ids missing from <paramref name="guildRoles"/> are ignored.
    /// </summary>
    public static RankRole? Resolve(IEnumerable<string> memberRoleIds, IReadOnlyDictionary<string, DiscordRole> guildRoles)
    {
        ArgumentNullException.ThrowIfNull(memberRoleIds);
        ArgumentNullException.ThrowIfNull(guildRoles);

        DiscordRole? best = null;
        foreach (var roleId in memberRoleIds)
        {
            if (!guildRoles.TryGetValue(roleId, out var role) || !role.IsRank || role.DeletedAt is not null) continue;
            if (best is null || Compare(role, best) < 0) best = role;
        }
        return best is null ? null : new RankRole(best.RoleId, best.Name, best.Color);
    }

    /// <summary>
    /// Rank order of two roles: negative when <paramref name="a"/> ranks above
    /// <paramref name="b"/>. RankOrder descending (a rank without an order comes last), then
    /// Position descending, then RoleId ordinal. Also sorts rank lists (rank distribution).
    /// </summary>
    public static int Compare(DiscordRole a, DiscordRole b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        var byOrder = Nullable.Compare(b.RankOrder, a.RankOrder);
        if (byOrder != 0) return byOrder;
        var byPosition = b.Position.CompareTo(a.Position);
        return byPosition != 0 ? byPosition : string.CompareOrdinal(a.RoleId, b.RoleId);
    }
}
```

- [ ] **Step 4: Run the rank resolver tests to verify they pass**

Run: `dotnet test src/Collector.Tests --filter "FullyQualifiedName~DiscordRankResolverTests"`

Expected: `Passed!  - Failed:     0, Passed:    11, Skipped:     0, Total:    11`

- [ ] **Step 5: Write the failing role list test**

Create `src/Collector.Tests/Discord/DiscordRoleListsTests.cs`:

```csharp
using Collector.Discord;
using FluentAssertions;
using Xunit;

namespace Collector.Tests.Discord;

/// <summary>
/// The two stored role lists: discord_members.RoleIdsJson (role id strings) and the values of
/// roles_changed events ({"id","name"} objects). A bad row reads as an empty list, never an error.
/// </summary>
public class DiscordRoleListsTests
{
    [Fact]
    public void RoleIds_AreReadFromTheStoredArray()
        => DiscordRoleLists.ParseRoleIds("""["123456789012345678","223456789012345678"]""")
            .Should().Equal("123456789012345678", "223456789012345678");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("\"123\"")]
    public void MalformedRoleIds_ReadAsEmpty(string? json)
        => DiscordRoleLists.ParseRoleIds(json).Should().BeEmpty();

    [Fact]
    public void NonStringRoleIds_AreSkipped()
        => DiscordRoleLists.ParseRoleIds("""[1, "2", null, ""]""").Should().Equal("2");

    [Fact]
    public void EventRoles_AreReadWithTheirNamesAtTheTime()
        => DiscordRoleLists.ParseEventRoles("""[{"id":"1","name":"Officier"},{"id":"2"}]""")
            .Should().Equal(new EventRole("1", "Officier"), new EventRole("2", ""));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("oops")]
    [InlineData("{\"id\":\"1\"}")]
    public void MalformedEventRoles_ReadAsEmpty(string? json)
        => DiscordRoleLists.ParseEventRoles(json).Should().BeEmpty();

    [Fact]
    public void EventEntriesWithoutAnId_AreSkipped()
        => DiscordRoleLists.ParseEventRoles("""[{"name":"x"}, 5, {"id":"7","name":"Pilote"}]""")
            .Should().Equal(new EventRole("7", "Pilote"));
}
```

- [ ] **Step 6: Run it to verify it fails**

Run: `dotnet test src/Collector.Tests --filter "FullyQualifiedName~DiscordRoleListsTests"`

Expected: the build fails with `error CS0103: The name 'DiscordRoleLists' does not exist in the current context` (and `error CS0246: The type or namespace name 'EventRole' could not be found`).

- [ ] **Step 7: Write the role list reader**

Create `src/Collector/Discord/DiscordRoleLists.cs`:

```csharp
using System.Text.Json;

namespace Collector.Discord;

/// <summary>A role as a roles_changed event stored it: its id and its name at the time.</summary>
public sealed record EventRole(string Id, string Name);

/// <summary>
/// Reads the two role lists stored as JSON: discord_members.RoleIdsJson (an array of role id
/// strings) and the values of roles_changed events (an array of {"id","name"} objects, spec
/// § 8). A malformed value reads as an empty list, so one bad row never fails a read.
/// </summary>
public static class DiscordRoleLists
{
    /// <summary>The role ids of a RoleIdsJson value, in stored order; non-string entries are skipped.</summary>
    public static IReadOnlyList<string> ParseRoleIds(string? roleIdsJson)
    {
        var ids = new List<string>();
        foreach (var item in ArrayItems(roleIdsJson))
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } id) ids.Add(id);
        }
        return ids;
    }

    /// <summary>The roles of a roles_changed value; an entry without a string id is skipped, a missing name reads as "".</summary>
    public static IReadOnlyList<EventRole> ParseEventRoles(string? eventValue)
    {
        var roles = new List<EventRole>();
        foreach (var item in ArrayItems(eventValue))
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("id", out var id)
                || id.ValueKind != JsonValueKind.String
                || id.GetString() is not { Length: > 0 } roleId)
            {
                continue;
            }
            var name = item.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "";
            roles.Add(new EventRole(roleId, name));
        }
        return roles;
    }

    private static List<JsonElement> ArrayItems(string? json)
    {
        var items = new List<JsonElement>();
        if (string.IsNullOrWhiteSpace(json)) return items;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return items;
            // Cloned: the elements outlive the document.
            items.AddRange(document.RootElement.EnumerateArray().Select(e => e.Clone()));
            return items;
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
```

- [ ] **Step 8: Run the role list tests to verify they pass**

Run: `dotnet test src/Collector.Tests --filter "FullyQualifiedName~DiscordRoleListsTests"`

Expected: `Passed!  - Failed:     0, Passed:    13, Skipped:     0, Total:    13`

- [ ] **Step 9: Write the failing tokenizer test**

Create `src/Collector.Tests/Discord/HandleTokenizerTests.cs` (emoji and invisible characters are written as escapes so the file survives any editor):

```csharp
using Collector.Discord;
using FluentAssertions;
using Xunit;

namespace Collector.Tests.Discord;

/// <summary>
/// Spec § 10.1 normalisation of Discord names into candidate RSI handles: bracketed segments
/// dropped, tokens split on anything outside [A-Za-z0-9_-], 3 to 60 characters, plus the whole
/// name without whitespace or emoji; distinct regardless of case.
/// </summary>
public class HandleTokenizerTests
{
    [Theory]
    [InlineData("[CORP] Pilote42")]
    [InlineData("(CORP) Pilote42")]
    [InlineData("{CORP} Pilote42")]
    [InlineData("«CORP» Pilote42")]
    [InlineData("Pilote42 [Officier]")]
    public void BracketedSegments_AreDropped(string value)
        => HandleTokenizer.Tokens(value).Should().Equal("Pilote42");

    [Fact]
    public void EveryKindOfBracket_IsDroppedInOneName()
        => HandleTokenizer.Tokens("[A] (afk) Pilote42 {x} «y»").Should().Equal("Pilote42");

    [Fact]
    public void AnUnclosedBracket_IsASeparator()
        => HandleTokenizer.Tokens("[CORP Pilote42").Should().Equal("CORP", "Pilote42", "[CORPPilote42");

    [Fact]
    public void Tokens_AreSplitOnEveryCharacterOutsideTheHandleAlphabet()
        => HandleTokenizer.Tokens("ace.pilot|sky/high:42")
            .Should().Equal("ace", "pilot", "sky", "high", "ace.pilot|sky/high:42");

    [Fact]
    public void UnderscoresAndHyphens_StayInsideAToken()
        => HandleTokenizer.Tokens("sky_high-42").Should().Equal("sky_high-42");

    [Fact]
    public void LettersOutsideAscii_SplitTokens()
        => HandleTokenizer.Tokens("Élodie").Should().Equal("lodie", "Élodie");

    [Theory]
    [InlineData("ab")]
    [InlineData("a b")]
    public void CandidatesShorterThanThree_AreDropped(string value)
        => HandleTokenizer.Tokens(value).Should().BeEmpty();

    [Fact]
    public void ShortWords_StillCountInTheWholeName()
        => HandleTokenizer.Tokens("ab cd").Should().Equal("abcd");

    [Fact]
    public void ThreeToSixtyCharacters_AreKept()
    {
        var sixty = new string('a', 60);

        HandleTokenizer.Tokens("abc").Should().Equal("abc");
        HandleTokenizer.Tokens(sixty).Should().Equal(sixty);
        HandleTokenizer.Tokens(new string('a', 61)).Should().BeEmpty();
        HandleTokenizer.Tokens("abc " + new string('b', 61)).Should().Equal("abc");
    }

    [Theory]
    [InlineData("Pilote 42")]
    [InlineData("\U0001F680 Pilote 42 \U0001F680")]
    [InlineData("Pilote\t42")]
    [InlineData("Pilote\u00A042")]
    public void TheWholeName_WithoutSpacesOrEmoji_IsACandidate(string value)
        => HandleTokenizer.Tokens(value).Should().Equal("Pilote", "Pilote42");

    [Theory]
    [InlineData("\U0001F469\U0001F3FD\u200D\U0001F680Ace99")]
    [InlineData("\U0001F1EB\U0001F1F7 Ace99")]
    [InlineData("\u202EAce99")]
    [InlineData("Ace99\u200D\uFE0F")]
    public void EmojiAndInvisibleCharacters_NeverReachACandidate(string value)
        => HandleTokenizer.Tokens(value).Should().Equal("Ace99");

    [Fact]
    public void AZeroWidthSpace_SplitsTokens_ButNotTheWholeName()
        => HandleTokenizer.Tokens("Pi\u200Blote").Should().Equal("lote", "Pilote");

    [Fact]
    public void ANameMadeOnlyOfEmoji_GivesNoCandidate()
        => HandleTokenizer.Tokens("\U0001F680\U0001F680\U0001F680").Should().BeEmpty();

    [Fact]
    public void Tokens_AreDistinctRegardlessOfCase_KeepingTheFirstSpelling()
        => HandleTokenizer.Tokens("Pilote42 PILOTE42 pilote42")
            .Should().Equal("Pilote42", "Pilote42PILOTE42pilote42");

    [Fact]
    public void Candidates_TakeTheNickThenTheGlobalNameThenTheUsername()
        => HandleTokenizer.Candidates("[CORP] Ace99", "Ace 99", "ace_99").Should().Equal("Ace99", "Ace", "ace_99");

    [Fact]
    public void Candidates_IgnoreCaseAcrossTheThreeNames()
        => HandleTokenizer.Candidates("Pilote42", "PILOTE42", "pilote42").Should().Equal("Pilote42");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoName_GivesNoToken(string? value)
        => HandleTokenizer.Tokens(value).Should().BeEmpty();

    [Fact]
    public void MissingNickAndGlobalName_LeaveTheUsername()
    {
        HandleTokenizer.Candidates(null, null, "ace99").Should().Equal("ace99");
        HandleTokenizer.Candidates(null, "   ", "ab").Should().BeEmpty();
    }
}
```

- [ ] **Step 10: Run it to verify it fails**

Run: `dotnet test src/Collector.Tests --filter "FullyQualifiedName~HandleTokenizerTests"`

Expected: the build fails with `error CS0103: The name 'HandleTokenizer' does not exist in the current context`.

- [ ] **Step 11: Write the tokenizer**

Create `src/Collector/Discord/HandleTokenizer.cs`:

```csharp
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
```

- [ ] **Step 12: Run the three test classes to verify they pass**

Run: `dotnet test src/Collector.Tests --filter "FullyQualifiedName~HandleTokenizerTests"`

Expected: `Passed!  - Failed:     0, Passed:    31, Skipped:     0, Total:    31`

Run: `dotnet test src/Collector.Tests --filter "FullyQualifiedName~Collector.Tests.Discord.DiscordRankResolverTests|FullyQualifiedName~Collector.Tests.Discord.DiscordRoleListsTests"`

Expected: `Passed!  - Failed:     0, Passed:    24, Skipped:     0, Total:    24`

- [ ] **Step 13: Commit**

```bash
git add src/Collector/Discord/DiscordRankResolver.cs \
        src/Collector/Discord/DiscordRoleLists.cs \
        src/Collector/Discord/HandleTokenizer.cs \
        src/Collector.Tests/Discord/DiscordRankResolverTests.cs \
        src/Collector.Tests/Discord/DiscordRoleListsTests.cs \
        src/Collector.Tests/Discord/HandleTokenizerTests.cs
git commit -F - <<'EOF'
feat(collector): resolve discord ranks and tokenize names for handle matching

The plugin sends raw Discord data. A member's rank is only the highest of
their live rank roles under the site's configuration, and a Discord name
only hints at an RSI handle once corpo tags, emoji, invisible characters
and separators are stripped. Keeping both rules pure in the collector
library lets every read (members, events, multi-membership, suggestions)
apply them the same way, lets a change of the rank configuration
reinterpret the whole history without rewriting it, and lets a malformed
stored role list read as empty instead of failing a page.

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
EOF
```

### Task C2: Reconciliation and discrepancies

**Files:**
- Create: `src/Collector.Api/Dtos/Discord/DiscordRosterDtos.cs`
- Create: `src/Collector.Api/Services/Discord/DiscordReconciliationService.cs`
- Create: `src/Collector.Api/Controllers/DiscordRostersController.cs`
- Modify: `src/Collector.Api/Extensions/ServiceCollectionExtensions.cs` (`AddApiServices`, after `services.AddHostedService<AudioOrphanSweeper>();`)
- Test: `src/Collector.Api.Tests/Discord/DiscordReadSeed.cs` (seeding helper shared by C2–C4)
- Test: `src/Collector.Api.Tests/Discord/DiscordReconciliationTests.cs`

**Interfaces:**
- Consumes:
  - CONTRACTS § 1 (lot A): entities `DiscordGuild`, `DiscordRole`, `DiscordAccount`, `DiscordMember` and DbSets `DiscordGuilds`, `DiscordRoles`, `DiscordAccounts`, `DiscordMembers`; index `IX_entity_links_Provider_Value`.
  - CONTRACTS § 5 (lot A): test kit `DiscordTestKit.NewSnowflake()`.
  - Task C1: `DiscordRankResolver.Resolve`, `RankRole`, `DiscordRoleLists.ParseRoleIds`.
  - Existing: `TrackerDbContext` (`OrganizationMembers`, `OrgMemberCounts`, `TrackedEntities`, `EntityLinks`), `LinkProviders.Discord`, `NotFoundException`, `Paging.Page`, `Paging.PageSize`, `PaginatedResponse<T>.Create(IReadOnlyList<T>, int, int, int)`, `ApiFactory.SignedInClientAsync`, `ApiCollection.Name`.
- Produces:
  - `Collector.Api.Services.Discord.DiscordReconciliationService` (scoped):
    - `Task<IReadOnlyDictionary<string, MemberReconciliation>> ReconcileAsync(string? orgSid, IReadOnlyCollection<ReconciliationSubject> subjects, CancellationToken ct)` — one entry per subject, keyed by Discord user id;
    - `Task<DiscordDiscrepanciesDto> GetDiscrepanciesAsync(string guildId, CancellationToken ct)` (404 `NotFoundException` for an unknown guild);
    - `Task<PaginatedResponse<DiscordMultiMemberDto>> GetMultiMembershipAsync(int page, int pageSize, CancellationToken ct)`;
    - `static string? RsiRankLabelOf(RankRole? rank, IReadOnlyDictionary<string, DiscordRole> roles)`;
    - `static bool RankIsCoherent(string? rsiRankLabel, string? rsiRank)`.
  - Records (same namespace): `ReconciliationSubject(string DiscordUserId, bool IsBot, string? RsiRankLabel)`, `MemberReconciliation(string? Status, bool MultipleLinks, IReadOnlyList<DiscordLinkedPersonDto> Links, string? RsiRank, DiscordLinkedPersonDto? Person)`.
  - DTO file `src/Collector.Api/Dtos/Discord/DiscordRosterDtos.cs` (file-scoped namespace `Collector.Api.Dtos.Discord`) with `DiscordReconciliationStatus` (constants + `IsValid`), `DiscordDiscrepancyKinds`, `DiscordLinkedPersonDto`, `DiscordDiscrepancyDto`, `DiscordTotalsDto`, `DiscordDiscrepanciesDto`, `DiscordMultiGuildDto`, `DiscordRsiOrgDto`, `DiscordMultiMemberDto` (CONTRACTS § 7 JSON).
  - `Collector.Api.Controllers.DiscordRostersController` — `[ApiController, Route("api"), Authorize]`, declared `public class DiscordRostersController : ControllerBase` (not partial: C5 adds `partial`), actions take their services with `[FromServices]` (no constructor). Routes `GET api/discord/guilds/{guildId}/discrepancies`, `GET api/discord/multi?page=&pageSize=`.
  - Test helper `Collector.Api.Tests.Discord.DiscordReadSeed(ApiFactory)`: `At`, `WithDbAsync`, `ReadAsync<T>`, `SeedOrgAsync`, `SeedRosterAsync`, `SeedCountsAsync`, `SeedPersonAsync`, `SeedGuildAsync`, `SeedRoleAsync`, `SeedMemberAsync`, `SetGuildOrgAsync`.

- [ ] **Step 1: Write the failing tests**

Create `src/Collector.Api.Tests/Discord/DiscordReadSeed.cs`:

```csharp
using System.Text.Json;
using Collector.Data;
using Collector.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Collector.Api.Tests.Discord;

/// <summary>
/// Seeds tracker.db straight through TrackerDbContext for the Discord read tests: RSI orgs,
/// rosters and counters, tracked people with their discord links, and guilds, roles,
/// accounts and members as the ingest leaves them. Every row is dated from <see cref="At"/>
/// so dates can be asserted exactly; ids come from DiscordTestKit.NewSnowflake().
/// </summary>
public sealed class DiscordReadSeed(ApiFactory factory)
{
    public static readonly DateTime At = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

    public async Task WithDbAsync(Func<TrackerDbContext, Task> action)
    {
        using var scope = factory.Services.CreateScope();
        await action(scope.ServiceProvider.GetRequiredService<TrackerDbContext>());
    }

    public async Task<T> ReadAsync<T>(Func<TrackerDbContext, Task<T>> read)
    {
        using var scope = factory.Services.CreateScope();
        return await read(scope.ServiceProvider.GetRequiredService<TrackerDbContext>());
    }

    public Task SeedOrgAsync(string sid, string name) => WithDbAsync(async db =>
    {
        db.Organizations.Add(new Organization { Sid = sid, Name = name, Timestamp = At });
        await db.SaveChangesAsync();
    });

    /// <summary>
    /// One roster row. IsActive has a database default of true, so EF takes an inserted false
    /// for "unset": an inactive row is inserted active, then updated.
    /// </summary>
    public Task SeedRosterAsync(string sid, string handle, int? citizenId, string? rank, bool active = true, int? stars = null)
        => WithDbAsync(async db =>
        {
            var row = new OrganizationMember
            {
                OrgSid = sid, UserHandle = handle, CitizenId = citizenId, Rank = rank, Stars = stars,
                Timestamp = At, IsActive = true,
            };
            db.OrganizationMembers.Add(row);
            await db.SaveChangesAsync();
            if (active) return;
            row.IsActive = false;
            await db.SaveChangesAsync();
        });

    public Task SeedCountsAsync(string sid, DateTime collectedAt, int totalRows, int? visible, int? redacted, int? hidden)
        => WithDbAsync(async db =>
        {
            db.OrgMemberCounts.Add(new OrgMemberCount
            {
                OrgSid = sid, CollectedAt = collectedAt, TotalRows = totalRows,
                VisibleCount = visible, RedactedCount = redacted, HiddenCount = hidden,
            });
            await db.SaveChangesAsync();
        });

    /// <summary>A tracked person, linked to each of <paramref name="discordUserIds"/>. Returns the entity id.</summary>
    public Task<long> SeedPersonAsync(int? citizenId, string handle, string? displayName, params string[] discordUserIds)
        => ReadAsync(async db =>
        {
            var entity = new TrackedEntity
            {
                CitizenId = citizenId, CurrentHandle = handle, DisplayName = displayName, CreatedAt = At, UpdatedAt = At,
            };
            db.TrackedEntities.Add(entity);
            await db.SaveChangesAsync();
            foreach (var discordUserId in discordUserIds)
            {
                db.EntityLinks.Add(new EntityLink
                {
                    TrackedEntityId = entity.Id, Provider = LinkProviders.Discord, Value = discordUserId,
                    AuthorApiUserId = 0, AuthorUsername = "seed", CreatedAt = At, UpdatedAt = At,
                });
            }
            await db.SaveChangesAsync();
            return entity.Id;
        });

    /// <summary>
    /// A guild synced at <see cref="At"/>. <paramref name="complete"/> sets LastCompleteSyncAt
    /// to the last sync's CollectedAt (At), otherwise leaves it null.
    /// </summary>
    public async Task<string> SeedGuildAsync(
        string? orgSid, string? name = null, bool complete = true, long? mappedByApiUserId = null, string? mappedByUsername = null)
    {
        var guildId = DiscordTestKit.NewSnowflake();
        await WithDbAsync(async db =>
        {
            db.DiscordGuilds.Add(new DiscordGuild
            {
                GuildId = guildId,
                Name = name ?? $"Guild {guildId}",
                OrgSid = orgSid,
                OrgMappedByApiUserId = orgSid is null ? null : mappedByApiUserId,
                OrgMappedByUsername = orgSid is null ? null : mappedByUsername,
                OrgMappedAt = orgSid is null ? null : At,
                FirstSyncAt = At.AddDays(-30),
                LastSyncAt = At,
                LastCollectedAt = At,
                LastCompleteSyncAt = complete ? At : null,
                CreatedAt = At,
                UpdatedAt = At,
            });
            await db.SaveChangesAsync();
        });
        return guildId;
    }

    /// <summary>A role of the guild; a rank without an explicit order takes its position, as on ingest.</summary>
    public async Task<string> SeedRoleAsync(
        string guildId, string name, int position, bool isRank, int? rankOrder = null, string? rsiRankLabel = null,
        bool deleted = false, string? color = "#e67e22")
    {
        var roleId = DiscordTestKit.NewSnowflake();
        await WithDbAsync(async db =>
        {
            db.DiscordRoles.Add(new DiscordRole
            {
                GuildId = guildId, RoleId = roleId, Name = name, Position = position, Color = color,
                Hoist = isRank, Managed = false, IsRank = isRank,
                RankOrder = isRank ? rankOrder ?? position : rankOrder,
                RsiRankLabel = rsiRankLabel, FirstSeenAt = At, LastSeenAt = At, DeletedAt = deleted ? At : null,
            });
            await db.SaveChangesAsync();
        });
        return roleId;
    }

    /// <summary>
    /// A member row of the guild and, unless it exists already (<paramref name="userId"/> of an
    /// account seeded before), its account. Returns the Discord user id.
    /// </summary>
    public async Task<string> SeedMemberAsync(
        string guildId, string username, IEnumerable<string>? roleIds = null, string? nick = null, string? globalName = null,
        bool bot = false, bool left = false, string? userId = null, DateTime? joinedAt = null)
    {
        var id = userId ?? DiscordTestKit.NewSnowflake();
        await WithDbAsync(async db =>
        {
            if (!await db.DiscordAccounts.AnyAsync(a => a.DiscordUserId == id))
            {
                db.DiscordAccounts.Add(new DiscordAccount
                {
                    DiscordUserId = id, Username = username, GlobalName = globalName, IsBot = bot, FirstSeenAt = At, LastSeenAt = At,
                });
            }
            db.DiscordMembers.Add(new DiscordMember
            {
                GuildId = guildId,
                DiscordUserId = id,
                Nick = nick,
                RoleIdsJson = JsonSerializer.Serialize((roleIds ?? []).Order(StringComparer.Ordinal).ToArray()),
                JoinedAt = joinedAt,
                FirstSeenAt = At,
                LastSeenAt = At,
                LeftAt = left ? At : null,
            });
            await db.SaveChangesAsync();
        });
        return id;
    }

    /// <summary>Re-maps a guild without going through the API (the PUT route arrives in C6).</summary>
    public Task SetGuildOrgAsync(string guildId, string? orgSid) => WithDbAsync(async db =>
    {
        var guild = await db.DiscordGuilds.SingleAsync(g => g.GuildId == guildId);
        guild.OrgSid = orgSid;
        guild.UpdatedAt = At;
        await db.SaveChangesAsync();
    });
}
```

Create `src/Collector.Api.Tests/Discord/DiscordReconciliationTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Collector.Api.Services.Discord;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Collector.Api.Tests.Discord;

/// <summary>
/// Reconciliation of Discord members with the RSI roster of the guild's org (spec § 10.2),
/// discrepancies and totals, and multi-membership (spec § 10.3).
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class DiscordReconciliationTests(ApiFactory factory)
{
    private static int _seq = 72_000;
    private readonly DiscordReadSeed _seed = new(factory);

    private static DateTime At => DiscordReadSeed.At;

    private static int Next() => Interlocked.Increment(ref _seq);

    /// <summary>A citizen id no other test uses.</summary>
    private static int Cid(int n, int k) => 1_720_000_000 + n * 20 + k;

    [Fact]
    public async Task EachStatus_IsComputedAgainstTheActiveRosterOfTheMappedOrg()
    {
        var n = Next();
        var sid = $"RCS{n}";
        await _seed.SeedRosterAsync(sid, $"OkPilot{n}", Cid(n, 1), "Officer");
        await _seed.SeedRosterAsync(sid, $"Mismatch{n}", Cid(n, 2), "Recruit");
        await _seed.SeedRosterAsync(sid, $"CasePilot{n}", null, "Officer");
        await _seed.SeedRosterAsync(sid, $"NewName{n}", Cid(n, 4), "Officer");
        await _seed.SeedRosterAsync(sid, $"Reused{n}", Cid(n, 6), "Officer");
        await _seed.SeedRosterAsync(sid, $"AnyIn{n}", Cid(n, 8), "Officer");
        await _seed.SeedRosterAsync(sid, $"NoLabel{n}", Cid(n, 9), "Whatever");
        await _seed.SeedRosterAsync(sid, $"Gone{n}", Cid(n, 10), "Officer", active: false);
        var ok = DiscordTestKit.NewSnowflake();
        var mismatch = DiscordTestKit.NewSnowflake();
        var notIn = DiscordTestKit.NewSnowflake();
        var byHandle = DiscordTestKit.NewSnowflake();
        var byCitizenId = DiscordTestKit.NewSnowflake();
        var reused = DiscordTestKit.NewSnowflake();
        var any = DiscordTestKit.NewSnowflake();
        var noLabel = DiscordTestKit.NewSnowflake();
        var gone = DiscordTestKit.NewSnowflake();
        var unlinked = DiscordTestKit.NewSnowflake();
        var bot = DiscordTestKit.NewSnowflake();
        await _seed.SeedPersonAsync(Cid(n, 1), $"OkPilot{n}", null, ok);
        await _seed.SeedPersonAsync(Cid(n, 2), $"Mismatch{n}", null, mismatch);
        await _seed.SeedPersonAsync(Cid(n, 3), $"Outsider{n}", null, notIn);
        // No citizen id on either side: matched by handle, regardless of case.
        await _seed.SeedPersonAsync(null, $"casepilot{n}", null, byHandle);
        // A stale handle on the entity: the citizen id is matched first.
        await _seed.SeedPersonAsync(Cid(n, 4), $"OldName{n}", null, byCitizenId);
        // The roster row with this handle belongs to another citizen.
        await _seed.SeedPersonAsync(Cid(n, 5), $"Reused{n}", null, reused);
        // Two people behind one account: one of them in the roster with a coherent rank is enough.
        await _seed.SeedPersonAsync(Cid(n, 7), $"AnyOut{n}", null, any);
        await _seed.SeedPersonAsync(Cid(n, 8), $"AnyIn{n}", null, any);
        await _seed.SeedPersonAsync(Cid(n, 9), $"NoLabel{n}", null, noLabel);
        await _seed.SeedPersonAsync(Cid(n, 10), $"Gone{n}", null, gone);
        await _seed.SeedPersonAsync(Cid(n, 11), $"BotOwner{n}", null, bot);
        ReconciliationSubject[] subjects =
        [
            new(ok, false, " officer "), new(mismatch, false, "Officer"), new(notIn, false, "Officer"),
            new(byHandle, false, "OFFICER"), new(byCitizenId, false, "Officer"), new(reused, false, "Officer"),
            new(any, false, "Officer"), new(noLabel, false, null), new(gone, false, "Officer"),
            new(unlinked, false, "Officer"), new(bot, true, "Officer"),
        ];

        var statuses = await ReconcileAsync(sid, subjects);

        statuses.ToDictionary(p => p.Key, p => p.Value.Status).Should().BeEquivalentTo(new Dictionary<string, string?>
        {
            [ok] = "ok",
            [mismatch] = "rank_mismatch",
            [notIn] = "not_in_rsi_org",
            [byHandle] = "ok",
            [byCitizenId] = "ok",
            [reused] = "not_in_rsi_org",
            [any] = "ok",
            [noLabel] = "ok",
            [gone] = "not_in_rsi_org",
            [unlinked] = "unlinked",
            [bot] = null,
        });
        statuses[ok].RsiRank.Should().Be("Officer");
        statuses[mismatch].RsiRank.Should().Be("Recruit");
        statuses[mismatch].Person!.Handle.Should().Be($"Mismatch{n}");
        statuses[noLabel].RsiRank.Should().Be("Whatever");
        statuses[notIn].RsiRank.Should().BeNull();
        statuses[any].MultipleLinks.Should().BeTrue();
        statuses[any].Links.Select(l => l.Handle).Should().Equal($"AnyOut{n}", $"AnyIn{n}");
        statuses[any].Person!.Handle.Should().Be($"AnyIn{n}");
        statuses.Where(p => p.Key != any).Should().OnlyContain(p => !p.Value.MultipleLinks);
        statuses[unlinked].Links.Should().BeEmpty();

        // An unmapped guild: no status at all, links still listed.
        var unmapped = await ReconcileAsync(null, subjects);
        unmapped.Values.Should().OnlyContain(r => r.Status == null);
        unmapped[any].Links.Should().HaveCount(2);
    }

    [Fact]
    public async Task AnOrgWithoutAnActiveRoster_LeavesEveryHumanRsiUnknown()
    {
        var n = Next();
        var sid = $"RCU{n}";
        await _seed.SeedRosterAsync(sid, $"Former{n}", Cid(n, 1), "Officer", active: false);
        var linked = DiscordTestKit.NewSnowflake();
        var unlinked = DiscordTestKit.NewSnowflake();
        var bot = DiscordTestKit.NewSnowflake();
        await _seed.SeedPersonAsync(Cid(n, 1), $"Former{n}", null, linked);

        var statuses = await ReconcileAsync(sid, [new(linked, false, null), new(unlinked, false, null), new(bot, true, null)]);

        statuses[linked].Status.Should().Be("rsi_unknown");
        statuses[unlinked].Status.Should().Be("rsi_unknown");
        statuses[bot].Status.Should().BeNull();
        statuses[linked].Links.Should().ContainSingle().Which.Handle.Should().Be($"Former{n}");
    }

    [Fact]
    public async Task Discrepancies_ListAbsencesRankMismatchesRsiOnly_AndTotalsFromTheLatestCounters()
    {
        var n = Next();
        var sid = $"RCD{n}";
        await _seed.SeedOrgAsync(sid, $"Org {sid}");
        await _seed.SeedRosterAsync(sid, $"Aligned{n}", Cid(n, 1), "Officer");
        await _seed.SeedRosterAsync(sid, $"Promoted{n}", Cid(n, 2), "Recruit");
        await _seed.SeedRosterAsync(sid, $"Departed{n}", Cid(n, 4), "Officer");
        await _seed.SeedRosterAsync(sid, $"Lonely{n}", null, "Recruit");
        await _seed.SeedCountsAsync(sid, At.AddDays(-2), totalRows: 10, visible: 7, redacted: 2, hidden: 1);
        // The latest reading could not see every row: never fall back to the older breakdown.
        await _seed.SeedCountsAsync(sid, At.AddDays(-1), totalRows: 12, visible: null, redacted: null, hidden: null);
        var guildId = await _seed.SeedGuildAsync(sid);
        var officier = await _seed.SeedRoleAsync(guildId, "Officier", 20, isRank: true, rsiRankLabel: "Officer");
        var recrue = await _seed.SeedRoleAsync(guildId, "Recrue", 10, isRank: true, rsiRankLabel: "Recruit");
        var pilote = await _seed.SeedRoleAsync(guildId, "Pilote", 30, isRank: false);
        var aligned = await _seed.SeedMemberAsync(guildId, $"aligned{n}", [officier, pilote]);
        var promoted = await _seed.SeedMemberAsync(guildId, $"promoted{n}", [officier], nick: $"[{sid}] Promoted");
        var outsider = await _seed.SeedMemberAsync(guildId, $"outsider{n}", [recrue], globalName: "Outsider");
        await _seed.SeedMemberAsync(guildId, $"newcomer{n}");
        var bot = await _seed.SeedMemberAsync(guildId, $"bot{n}", [officier], bot: true);
        var departed = await _seed.SeedMemberAsync(guildId, $"departed{n}", [officier], left: true);
        await _seed.SeedPersonAsync(Cid(n, 1), $"Aligned{n}", null, aligned);
        await _seed.SeedPersonAsync(Cid(n, 2), $"Promoted{n}", null, promoted);
        await _seed.SeedPersonAsync(Cid(n, 3), $"Outsider{n}", null, outsider);
        await _seed.SeedPersonAsync(Cid(n, 4), $"Departed{n}", null, departed);
        await _seed.SeedPersonAsync(Cid(n, 5), $"BotOwner{n}", null, bot);
        var client = await factory.SignedInClientAsync($"c2-gaps-{n}");

        var body = await client.GetFromJsonAsync<JsonElement>($"/api/discord/guilds/{guildId}/discrepancies");

        body.GetProperty("orgSid").GetString().Should().Be(sid);
        body.GetProperty("rsiOnlyAvailable").GetBoolean().Should().BeTrue();
        Items(body).Should().Equal(
            ("not_in_rsi_org", $"Outsider{n}", Cid(n, 3), outsider, "Outsider", "Recrue", null),
            ("rank_mismatch", $"Promoted{n}", Cid(n, 2), promoted, $"[{sid}] Promoted", "Officier", "Recruit"),
            // Linked, but to an account that left the server.
            ("rsi_only", $"Departed{n}", Cid(n, 4), null, null, null, "Officer"),
            ("rsi_only", $"Lonely{n}", null, null, null, null, "Recruit"));
        var totals = body.GetProperty("totals");
        totals.GetProperty("discordActive").GetInt32().Should().Be(4, "bots and departed members are not counted");
        totals.GetProperty("discordLinked").GetInt32().Should().Be(3);
        totals.GetProperty("rsiTotalRows").GetInt32().Should().Be(12);
        totals.GetProperty("rsiVisible").ValueKind.Should().Be(JsonValueKind.Null);
        totals.GetProperty("rsiRedacted").ValueKind.Should().Be(JsonValueKind.Null);
        totals.GetProperty("rsiHidden").ValueKind.Should().Be(JsonValueKind.Null);
        totals.GetProperty("rsiBreakdownKnown").GetBoolean().Should().BeFalse();
        totals.GetProperty("rsiCountsAt").GetDateTime().Should().Be(At.AddDays(-1));
    }

    [Fact]
    public async Task WithoutACompleteSync_RsiOnlyIsNotComputed()
    {
        var n = Next();
        var sid = $"RCP{n}";
        await _seed.SeedRosterAsync(sid, $"Lonely{n}", Cid(n, 1), "Officer");
        await _seed.SeedCountsAsync(sid, At, totalRows: 5, visible: 3, redacted: 1, hidden: 1);
        var guildId = await _seed.SeedGuildAsync(sid, complete: false);
        var outsider = await _seed.SeedMemberAsync(guildId, $"outsider{n}");
        await _seed.SeedPersonAsync(Cid(n, 2), $"Outsider{n}", null, outsider);
        var client = await factory.SignedInClientAsync($"c2-partial-{n}");

        var body = await client.GetFromJsonAsync<JsonElement>($"/api/discord/guilds/{guildId}/discrepancies");

        body.GetProperty("rsiOnlyAvailable").GetBoolean().Should().BeFalse();
        Items(body).Should().Equal(("not_in_rsi_org", $"Outsider{n}", Cid(n, 2), outsider, $"outsider{n}", null, null));
        var totals = body.GetProperty("totals");
        totals.GetProperty("rsiBreakdownKnown").GetBoolean().Should().BeTrue();
        totals.GetProperty("rsiVisible").GetInt32().Should().Be(3);
        totals.GetProperty("rsiRedacted").GetInt32().Should().Be(1);
        totals.GetProperty("rsiHidden").GetInt32().Should().Be(1);
        totals.GetProperty("rsiTotalRows").GetInt32().Should().Be(5);
    }

    [Fact]
    public async Task AnOrgWithoutRoster_GivesNoDiscrepancy_ButDiscordTotals()
    {
        var n = Next();
        var sid = $"RCN{n}";
        await _seed.SeedOrgAsync(sid, $"Org {sid}");
        var guildId = await _seed.SeedGuildAsync(sid);
        var member = await _seed.SeedMemberAsync(guildId, $"member{n}");
        await _seed.SeedPersonAsync(Cid(n, 1), $"Member{n}", null, member);
        var client = await factory.SignedInClientAsync($"c2-noroster-{n}");

        var body = await client.GetFromJsonAsync<JsonElement>($"/api/discord/guilds/{guildId}/discrepancies");

        Items(body).Should().BeEmpty();
        var totals = body.GetProperty("totals");
        totals.GetProperty("discordActive").GetInt32().Should().Be(1);
        totals.GetProperty("discordLinked").GetInt32().Should().Be(1);
        totals.GetProperty("rsiTotalRows").ValueKind.Should().Be(JsonValueKind.Null);
        totals.GetProperty("rsiCountsAt").ValueKind.Should().Be(JsonValueKind.Null);
        totals.GetProperty("rsiBreakdownKnown").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task AnUnmappedGuild_HasNoDiscrepancies_AndAnUnknownGuildIs404()
    {
        var n = Next();
        var guildId = await _seed.SeedGuildAsync(null);
        await _seed.SeedMemberAsync(guildId, $"member{n}");
        var client = await factory.SignedInClientAsync($"c2-unmapped-{n}");

        var body = await client.GetFromJsonAsync<JsonElement>($"/api/discord/guilds/{guildId}/discrepancies");
        var unknown = await client.GetAsync($"/api/discord/guilds/{DiscordTestKit.NewSnowflake()}/discrepancies");

        body.GetProperty("orgSid").ValueKind.Should().Be(JsonValueKind.Null);
        body.GetProperty("rsiOnlyAvailable").GetBoolean().Should().BeFalse();
        body.GetProperty("items").GetArrayLength().Should().Be(0);
        body.GetProperty("totals").ValueKind.Should().Be(JsonValueKind.Null);
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ARemappedGuild_IsReconciledAgainstItsNewOrg()
    {
        var n = Next();
        var sidA = $"RCA{n}";
        var sidB = $"RCB{n}";
        await _seed.SeedRosterAsync(sidA, $"Alpha{n}", Cid(n, 1), "Officer");
        await _seed.SeedRosterAsync(sidB, $"Bravo{n}", Cid(n, 2), "Officer");
        await _seed.SeedCountsAsync(sidA, At, totalRows: 5, visible: 5, redacted: 0, hidden: 0);
        await _seed.SeedCountsAsync(sidB, At, totalRows: 9, visible: 9, redacted: 0, hidden: 0);
        var guildId = await _seed.SeedGuildAsync(sidA);
        var member = await _seed.SeedMemberAsync(guildId, $"remap{n}");
        await _seed.SeedPersonAsync(Cid(n, 1), $"Alpha{n}", null, member);
        var client = await factory.SignedInClientAsync($"c2-remap-{n}");
        var url = $"/api/discord/guilds/{guildId}/discrepancies";

        var before = await client.GetFromJsonAsync<JsonElement>(url);
        await _seed.SetGuildOrgAsync(guildId, sidB);
        var after = await client.GetFromJsonAsync<JsonElement>(url);

        before.GetProperty("orgSid").GetString().Should().Be(sidA);
        Items(before).Should().BeEmpty();
        before.GetProperty("totals").GetProperty("rsiTotalRows").GetInt32().Should().Be(5);
        after.GetProperty("orgSid").GetString().Should().Be(sidB);
        Items(after).Should().Equal(
            ("not_in_rsi_org", $"Alpha{n}", Cid(n, 1), member, $"remap{n}", null, null),
            ("rsi_only", $"Bravo{n}", Cid(n, 2), null, null, null, "Officer"));
        after.GetProperty("totals").GetProperty("rsiTotalRows").GetInt32().Should().Be(9);
    }

    [Fact]
    public async Task MultiMembership_ListsHumansActiveInTwoGuilds_WithTheRsiOrgsOfTheirLinkedPeople()
    {
        var n = Next();
        var sidX = $"RMX{n}";
        var sidY = $"RMY{n}";
        var sidZ = $"RMZ{n}";
        await _seed.SeedRosterAsync(sidX, $"Main{n}", Cid(n, 1), "Boss");
        // A person without citizen id, found by handle regardless of case.
        await _seed.SeedRosterAsync(sidY, $"ALT{n}", null, "Grunt");
        await _seed.SeedRosterAsync(sidZ, $"Main{n}", Cid(n, 1), "Old", active: false);
        var alpha = await _seed.SeedGuildAsync(sidX, name: $"Alpha guild {n}");
        var bravo = await _seed.SeedGuildAsync(null, name: $"Bravo guild {n}");
        var officier = await _seed.SeedRoleAsync(alpha, "Officier", 20, isRank: true);
        var multi = await _seed.SeedMemberAsync(alpha, $"c2multi{n}", [officier], globalName: "Multi");
        await _seed.SeedMemberAsync(bravo, $"c2multi{n}", userId: multi);
        var loner = await _seed.SeedMemberAsync(alpha, $"c2loner{n}");
        await _seed.SeedMemberAsync(bravo, $"c2loner{n}", userId: loner);
        var leaver = await _seed.SeedMemberAsync(alpha, $"c2leaver{n}");
        await _seed.SeedMemberAsync(bravo, $"c2leaver{n}", userId: leaver, left: true);
        var bot = await _seed.SeedMemberAsync(alpha, $"c2bot{n}", bot: true);
        await _seed.SeedMemberAsync(bravo, $"c2bot{n}", userId: bot);
        await _seed.SeedPersonAsync(Cid(n, 1), $"Main{n}", "Main", multi);
        await _seed.SeedPersonAsync(null, $"Alt{n}", null, multi);
        var client = await factory.SignedInClientAsync($"c2-multi-{n}");

        var all = await AllMultiAsync(client);

        var ids = all.Select(i => i.GetProperty("discordUserId").GetString()).ToList();
        ids.Should().Contain(new[] { multi, loner }).And.NotContain(new[] { leaver, bot });
        var item = all.Single(i => i.GetProperty("discordUserId").GetString() == multi);
        item.GetProperty("username").GetString().Should().Be($"c2multi{n}");
        item.GetProperty("globalName").GetString().Should().Be("Multi");
        item.GetProperty("guilds").EnumerateArray()
            .Select(g => (g.GetProperty("guildId").GetString(), g.GetProperty("guildName").GetString(),
                g.GetProperty("orgSid").GetString(), g.GetProperty("rank").GetString()))
            .Should().Equal((alpha, $"Alpha guild {n}", sidX, "Officier"), (bravo, $"Bravo guild {n}", null, null));
        item.GetProperty("links").EnumerateArray().Select(l => l.GetProperty("handle").GetString())
            .Should().Equal($"Main{n}", $"Alt{n}");
        item.GetProperty("rsiOrgs").EnumerateArray()
            .Select(o => (o.GetProperty("sid").GetString(), o.GetProperty("rank").GetString()))
            .Should().Equal((sidX, "Boss"), (sidY, "Grunt"));
        var lonerItem = all.Single(i => i.GetProperty("discordUserId").GetString() == loner);
        lonerItem.GetProperty("links").GetArrayLength().Should().Be(0);
        lonerItem.GetProperty("rsiOrgs").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task MultiMembership_IsPagedByAccount()
    {
        var n = Next();
        var first = await _seed.SeedGuildAsync(null);
        var second = await _seed.SeedGuildAsync(null);
        for (var k = 0; k < 3; k++)
        {
            var id = await _seed.SeedMemberAsync(first, $"c2page{n}-{k}");
            await _seed.SeedMemberAsync(second, $"c2page{n}-{k}", userId: id);
        }
        var client = await factory.SignedInClientAsync($"c2-multipage-{n}");

        var one = await client.GetFromJsonAsync<JsonElement>("/api/discord/multi?page=1&pageSize=1");
        var two = await client.GetFromJsonAsync<JsonElement>("/api/discord/multi?page=2&pageSize=1");

        one.GetProperty("items").GetArrayLength().Should().Be(1);
        two.GetProperty("items").GetArrayLength().Should().Be(1);
        one.GetProperty("pageSize").GetInt32().Should().Be(1);
        one.GetProperty("total").GetInt32().Should().BeGreaterThanOrEqualTo(3);
        one.GetProperty("totalPages").GetInt32().Should().Be(one.GetProperty("total").GetInt32());
        one.GetProperty("items")[0].GetProperty("discordUserId").GetString()
            .Should().NotBe(two.GetProperty("items")[0].GetProperty("discordUserId").GetString());
    }

    private async Task<IReadOnlyDictionary<string, MemberReconciliation>> ReconcileAsync(
        string? orgSid, IReadOnlyCollection<ReconciliationSubject> subjects)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<DiscordReconciliationService>()
            .ReconcileAsync(orgSid, subjects, CancellationToken.None);
    }

    /// <summary>Every multi-membership row, walking the pages (other tests add rows to the shared database).</summary>
    private static async Task<List<JsonElement>> AllMultiAsync(HttpClient client)
    {
        var all = new List<JsonElement>();
        for (var page = 1; ; page++)
        {
            var body = await client.GetFromJsonAsync<JsonElement>($"/api/discord/multi?page={page}&pageSize=200");
            all.AddRange(body.GetProperty("items").EnumerateArray());
            if (page >= body.GetProperty("totalPages").GetInt32()) return all;
        }
    }

    private static List<(string? Kind, string? Handle, int? CitizenId, string? DiscordUserId, string? DiscordName,
        string? DiscordRank, string? RsiRank)> Items(JsonElement body)
        => body.GetProperty("items").EnumerateArray()
            .Select(i => (
                i.GetProperty("kind").GetString(),
                i.GetProperty("handle").GetString(),
                Int(i.GetProperty("citizenId")),
                i.GetProperty("discordUserId").GetString(),
                i.GetProperty("discordName").GetString(),
                i.GetProperty("discordRank").GetString(),
                i.GetProperty("rsiRank").GetString()))
            .ToList();

    private static int? Int(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null : value.GetInt32();
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test src/Collector.Api.Tests --filter "FullyQualifiedName~DiscordReconciliationTests"`

Expected: the build fails with `error CS0246: The type or namespace name 'ReconciliationSubject' could not be found` (also `MemberReconciliation`, `DiscordReconciliationService`).

- [ ] **Step 3: Write the DTOs**

Create `src/Collector.Api/Dtos/Discord/DiscordRosterDtos.cs`:

```csharp
namespace Collector.Api.Dtos.Discord;

/// <summary>
/// Statuses of an active, non-bot Discord member against the active RSI roster of the org
/// the guild is mapped to (spec § 10.2).
/// </summary>
public static class DiscordReconciliationStatus
{
    /// <summary>The org has no active organization_members row: its roster was never collected.</summary>
    public const string RsiUnknown = "rsi_unknown";

    /// <summary>Nobody is linked to the member's Discord id.</summary>
    public const string Unlinked = "unlinked";

    /// <summary>A linked person is in the roster with a coherent rank, or the Discord rank has no RSI equivalent.</summary>
    public const string Ok = "ok";

    /// <summary>A linked person is in the roster, but none with a coherent rank.</summary>
    public const string RankMismatch = "rank_mismatch";

    /// <summary>No linked person is in the roster: absent from the RSI org, or hidden there.</summary>
    public const string NotInRsiOrg = "not_in_rsi_org";

    public static bool IsValid(string? value) => value is RsiUnknown or Unlinked or Ok or RankMismatch or NotInRsiOrg;
}

/// <summary>Kinds of <see cref="DiscordDiscrepancyDto"/>.</summary>
public static class DiscordDiscrepancyKinds
{
    /// <summary>In the active RSI roster, with no linked Discord id active on the guild.</summary>
    public const string RsiOnly = "rsi_only";

    public const string NotInRsiOrg = DiscordReconciliationStatus.NotInRsiOrg;
    public const string RankMismatch = DiscordReconciliationStatus.RankMismatch;
}

/// <summary>A tracked person linked to a Discord account through an entity_links "discord" row.</summary>
public sealed class DiscordLinkedPersonDto
{
    public string? Handle { get; set; }
    public int? CitizenId { get; set; }
    public string? DisplayName { get; set; }
}

/// <summary>One gap between a guild and the RSI roster of its org.</summary>
public sealed class DiscordDiscrepancyDto
{
    /// <summary><see cref="DiscordDiscrepancyKinds"/> value.</summary>
    public string Kind { get; set; } = null!;

    /// <summary>The RSI person concerned: the linked person, or the roster member for rsi_only.</summary>
    public string? Handle { get; set; }

    public int? CitizenId { get; set; }

    /// <summary>Null for rsi_only.</summary>
    public string? DiscordUserId { get; set; }

    /// <summary>Nick, else global name, else username. Null for rsi_only.</summary>
    public string? DiscordName { get; set; }

    /// <summary>Name of the member's Discord rank.</summary>
    public string? DiscordRank { get; set; }

    /// <summary>Rank in the RSI roster, when the person is in it.</summary>
    public string? RsiRank { get; set; }
}

/// <summary>Headcounts of a guild and of its org's RSI roster (latest org_member_counts row only).</summary>
public sealed class DiscordTotalsDto
{
    /// <summary>Active, non-bot members.</summary>
    public int DiscordActive { get; set; }

    /// <summary>Active, non-bot members linked to at least one person.</summary>
    public int DiscordLinked { get; set; }

    public int? RsiVisible { get; set; }
    public int? RsiRedacted { get; set; }
    public int? RsiHidden { get; set; }
    public int? RsiTotalRows { get; set; }
    public DateTime? RsiCountsAt { get; set; }

    /// <summary>False when the latest reading has no breakdown: show RsiTotalRows alone ("répartition inconnue").</summary>
    public bool RsiBreakdownKnown { get; set; }
}

/// <summary>Answer of GET api/discord/guilds/{guildId}/discrepancies.</summary>
public sealed class DiscordDiscrepanciesDto
{
    /// <summary>Null for an unmapped guild, which has no items and no totals.</summary>
    public string? OrgSid { get; set; }

    /// <summary>False until a complete sync proves absences: rsi_only items are then not computed.</summary>
    public bool RsiOnlyAvailable { get; set; }

    public IReadOnlyList<DiscordDiscrepancyDto> Items { get; set; } = [];
    public DiscordTotalsDto? Totals { get; set; }
}

/// <summary>A guild a multi-member is active in.</summary>
public sealed class DiscordMultiGuildDto
{
    public string GuildId { get; set; } = null!;
    public string GuildName { get; set; } = null!;
    public string? OrgSid { get; set; }

    /// <summary>Name of the member's rank in that guild.</summary>
    public string? Rank { get; set; }
}

/// <summary>An RSI org one of the linked people is an active member of.</summary>
public sealed class DiscordRsiOrgDto
{
    public string Sid { get; set; } = null!;
    public string? Rank { get; set; }
}

/// <summary>A non-bot account active in at least two tracked guilds (spec § 10.3).</summary>
public sealed class DiscordMultiMemberDto
{
    public string DiscordUserId { get; set; } = null!;
    public string Username { get; set; } = null!;
    public string? GlobalName { get; set; }
    public IReadOnlyList<DiscordMultiGuildDto> Guilds { get; set; } = [];
    public IReadOnlyList<DiscordLinkedPersonDto> Links { get; set; } = [];

    /// <summary>Union of the active RSI orgs of every linked person, with their rank there.</summary>
    public IReadOnlyList<DiscordRsiOrgDto> RsiOrgs { get; set; } = [];
}
```

- [ ] **Step 4: Write the reconciliation service**

Create `src/Collector.Api/Services/Discord/DiscordReconciliationService.cs`:

```csharp
using System.Globalization;
using Collector.Api.Dtos.Common;
using Collector.Api.Dtos.Discord;
using Collector.Api.Errors;
using Collector.Api.Extensions;
using Collector.Data;
using Collector.Discord;
using Collector.Models;
using Microsoft.EntityFrameworkCore;

namespace Collector.Api.Services.Discord;

/// <summary>
/// A Discord member to reconcile: their id, whether they are a bot, and the RSI rank their
/// Discord rank stands for (the RsiRankLabel of their rank role), when configured.
/// </summary>
public sealed record ReconciliationSubject(string DiscordUserId, bool IsBot, string? RsiRankLabel);

/// <summary>
/// A member's reconciliation (spec § 10.2). <see cref="Status"/> is a
/// <see cref="DiscordReconciliationStatus"/> value, null for a bot or in an unmapped guild.
/// <see cref="Person"/> is the linked person the status is about: the one found in the roster
/// (with a coherent rank first), else the first linked person.
/// </summary>
public sealed record MemberReconciliation(
    string? Status,
    bool MultipleLinks,
    IReadOnlyList<DiscordLinkedPersonDto> Links,
    string? RsiRank,
    DiscordLinkedPersonDto? Person);

/// <summary>
/// Reconciles Discord members with the active RSI roster of the org their guild is mapped to
/// (spec § 10.2), lists the discrepancies of a guild, and the accounts active in several
/// tracked guilds (spec § 10.3). Everything is computed on read from the guild's current
/// mapping: re-mapping a guild re-reconciles it at once. A member is linked through every
/// entity_links "discord" row carrying their id; a linked person is in the roster when an
/// active row carries their citizen id, else (and only then) their handle regardless of case,
/// unless that row belongs to another known citizen.
/// </summary>
public sealed class DiscordReconciliationService(TrackerDbContext db)
{
    private static readonly List<LinkedPerson> NoPersons = [];

    /// <summary>The RSI rank a member's Discord rank stands for, when configured.</summary>
    public static string? RsiRankLabelOf(RankRole? rank, IReadOnlyDictionary<string, DiscordRole> roles)
        => rank is not null && roles.TryGetValue(rank.RoleId, out var role) ? role.RsiRankLabel : null;

    /// <summary>Ranks are compared trimmed and regardless of case; no RSI equivalent configured is always coherent.</summary>
    public static bool RankIsCoherent(string? rsiRankLabel, string? rsiRank)
        => string.IsNullOrWhiteSpace(rsiRankLabel)
           || string.Equals(rsiRankLabel.Trim(), rsiRank?.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reconciles <paramref name="subjects"/> against the active roster of
    /// <paramref name="orgSid"/> (null: unmapped guild, no status). Reads the links of the
    /// subjects, then only the roster rows of the people linked to them: a page of members
    /// costs a handful of queries whatever the size of the org.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, MemberReconciliation>> ReconcileAsync(
        string? orgSid, IReadOnlyCollection<ReconciliationSubject> subjects, CancellationToken ct)
    {
        if (subjects.Count == 0) return new Dictionary<string, MemberReconciliation>(StringComparer.Ordinal);

        var links = await LoadLinksAsync(subjects.Select(s => s.DiscordUserId), ct);
        RosterIndex? roster = null;
        if (orgSid is not null && subjects.Any(s => !s.IsBot)
            && await db.OrganizationMembers.AnyAsync(m => m.OrgSid == orgSid && m.IsActive, ct))
        {
            roster = new RosterIndex(await LoadRosterOfAsync(orgSid, links.Values.SelectMany(p => p).ToList(), ct));
        }
        return Reconcile(orgSid, subjects, links, roster);
    }

    /// <summary>
    /// Discrepancies and totals of a guild. Unmapped: <c>{ orgSid: null, items: [], totals: null }</c>.
    /// rsi_only items are only computed once a complete sync exists (the only one that proves
    /// absences). Order: not_in_rsi_org, rank_mismatch (by Discord name), then rsi_only (by handle).
    /// </summary>
    public async Task<DiscordDiscrepanciesDto> GetDiscrepanciesAsync(string guildId, CancellationToken ct)
    {
        var guild = await db.DiscordGuilds.AsNoTracking()
                .Where(g => g.GuildId == guildId)
                .Select(g => new { g.OrgSid, g.LastCompleteSyncAt })
                .FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException("Serveur Discord inconnu.");
        if (guild.OrgSid is null)
            return new DiscordDiscrepanciesDto { OrgSid = null, RsiOnlyAvailable = false, Items = [], Totals = null };
        var orgSid = guild.OrgSid;

        var members = await (
                from m in db.DiscordMembers.AsNoTracking()
                join a in db.DiscordAccounts.AsNoTracking() on m.DiscordUserId equals a.DiscordUserId
                where m.GuildId == guildId && m.LeftAt == null && !a.IsBot
                select new { m.DiscordUserId, m.Nick, m.RoleIdsJson, a.Username, a.GlobalName })
            .ToListAsync(ct);
        var roles = await LoadRolesAsync(guildId, ct);
        var rosterRows = await db.OrganizationMembers.AsNoTracking()
            .Where(m => m.OrgSid == orgSid && m.IsActive)
            .Select(m => new RosterRow(m.OrgSid, m.UserHandle, m.CitizenId, m.Rank))
            .ToListAsync(ct);
        var roster = rosterRows.Count == 0 ? null : new RosterIndex(rosterRows);
        var links = await LoadLinksAsync(members.Select(m => m.DiscordUserId), ct);

        var ranks = members.ToDictionary(
            m => m.DiscordUserId,
            m => DiscordRankResolver.Resolve(DiscordRoleLists.ParseRoleIds(m.RoleIdsJson), roles),
            StringComparer.Ordinal);
        var statuses = Reconcile(
            orgSid,
            members.Select(m => new ReconciliationSubject(m.DiscordUserId, false, RsiRankLabelOf(ranks[m.DiscordUserId], roles))),
            links,
            roster);

        var items = new List<DiscordDiscrepancyDto>();
        foreach (var member in members)
        {
            var reconciled = statuses[member.DiscordUserId];
            if (reconciled.Status is not (DiscordReconciliationStatus.NotInRsiOrg or DiscordReconciliationStatus.RankMismatch))
                continue;
            items.Add(new DiscordDiscrepancyDto
            {
                Kind = reconciled.Status!,
                Handle = reconciled.Person?.Handle,
                CitizenId = reconciled.Person?.CitizenId,
                DiscordUserId = member.DiscordUserId,
                DiscordName = member.Nick ?? member.GlobalName ?? member.Username,
                DiscordRank = ranks[member.DiscordUserId]?.Name,
                RsiRank = reconciled.RsiRank,
            });
        }

        var rsiOnlyAvailable = guild.LastCompleteSyncAt is not null;
        if (rsiOnlyAvailable && roster is not null)
        {
            // Covered: a roster row one of the active members' linked people matches.
            var covered = new HashSet<RosterRow>(links.Values.SelectMany(p => p).SelectMany(p => roster.Match(p)));
            items.AddRange(roster.Rows
                .Where(r => !covered.Contains(r))
                .DistinctBy(PersonKey)
                .Select(r => new DiscordDiscrepancyDto
                {
                    Kind = DiscordDiscrepancyKinds.RsiOnly, Handle = r.Handle, CitizenId = r.CitizenId, RsiRank = r.Rank,
                }));
        }

        var counts = await db.OrgMemberCounts.AsNoTracking()
            .Where(c => c.OrgSid == orgSid)
            .OrderByDescending(c => c.CollectedAt).ThenByDescending(c => c.Id)
            .FirstOrDefaultAsync(ct);
        var breakdownKnown = counts?.VisibleCount is not null;

        return new DiscordDiscrepanciesDto
        {
            OrgSid = orgSid,
            RsiOnlyAvailable = rsiOnlyAvailable,
            Items = items
                .OrderBy(i => KindOrder(i.Kind))
                .ThenBy(i => i.DiscordName ?? i.Handle ?? "", StringComparer.OrdinalIgnoreCase)
                .ThenBy(i => i.DiscordUserId ?? i.Handle ?? "", StringComparer.Ordinal)
                .ToList(),
            Totals = new DiscordTotalsDto
            {
                DiscordActive = members.Count,
                DiscordLinked = members.Count(m => links.ContainsKey(m.DiscordUserId)),
                RsiVisible = breakdownKnown ? counts!.VisibleCount : null,
                RsiRedacted = breakdownKnown ? counts!.RedactedCount : null,
                RsiHidden = breakdownKnown ? counts!.HiddenCount : null,
                RsiTotalRows = counts?.TotalRows,
                RsiCountsAt = counts?.CollectedAt,
                RsiBreakdownKnown = breakdownKnown,
            },
        };
    }

    /// <summary>
    /// Non-bot accounts active in at least two tracked guilds, by username, paged by the
    /// database; for each, their active guilds with org and rank, their links, and the union
    /// of the active RSI orgs of every linked person with their rank there.
    /// </summary>
    public async Task<PaginatedResponse<DiscordMultiMemberDto>> GetMultiMembershipAsync(
        int page, int pageSize, CancellationToken ct)
    {
        page = Paging.Page(page);
        pageSize = Paging.PageSize(pageSize);

        var inSeveralGuilds = db.DiscordMembers
            .Where(m => m.LeftAt == null)
            .GroupBy(m => m.DiscordUserId)
            .Where(g => g.Count() >= 2)
            .Select(g => g.Key);
        var accountsQuery = db.DiscordAccounts.AsNoTracking()
            .Where(a => !a.IsBot && inSeveralGuilds.Contains(a.DiscordUserId));
        var total = await accountsQuery.CountAsync(ct);
        var accounts = await accountsQuery
            .OrderBy(a => a.Username).ThenBy(a => a.DiscordUserId)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(a => new { a.DiscordUserId, a.Username, a.GlobalName })
            .ToListAsync(ct);
        if (accounts.Count == 0)
            return PaginatedResponse<DiscordMultiMemberDto>.Create(Array.Empty<DiscordMultiMemberDto>(), page, pageSize, total);

        var ids = accounts.Select(a => a.DiscordUserId).ToList();
        var memberships = await (
                from m in db.DiscordMembers.AsNoTracking()
                join g in db.DiscordGuilds.AsNoTracking() on m.GuildId equals g.GuildId
                where m.LeftAt == null && ids.Contains(m.DiscordUserId)
                select new { m.DiscordUserId, m.GuildId, GuildName = g.Name, g.OrgSid, m.RoleIdsJson })
            .ToListAsync(ct);
        var guildIds = memberships.Select(m => m.GuildId).Distinct(StringComparer.Ordinal).ToList();
        var rolesByGuild = (await db.DiscordRoles.AsNoTracking().Where(r => guildIds.Contains(r.GuildId)).ToListAsync(ct))
            .GroupBy(r => r.GuildId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToDictionary(r => r.RoleId, StringComparer.Ordinal), StringComparer.Ordinal);
        var links = await LoadLinksAsync(ids, ct);
        var persons = links.Values.SelectMany(p => p).DistinctBy(p => p.EntityId).ToList();
        var rosters = (await LoadActiveOrgRowsAsync(persons, ct))
            .GroupBy(r => r.OrgSid, StringComparer.OrdinalIgnoreCase)
            .Select(g => new RosterIndex(g))
            .ToList();

        var items = accounts.Select(a =>
        {
            var linked = links.TryGetValue(a.DiscordUserId, out var found) ? found : NoPersons;
            return new DiscordMultiMemberDto
            {
                DiscordUserId = a.DiscordUserId,
                Username = a.Username,
                GlobalName = a.GlobalName,
                Guilds = memberships
                    .Where(m => m.DiscordUserId == a.DiscordUserId)
                    .OrderBy(m => m.GuildName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(m => m.GuildId, StringComparer.Ordinal)
                    .Select(m => new DiscordMultiGuildDto
                    {
                        GuildId = m.GuildId,
                        GuildName = m.GuildName,
                        OrgSid = m.OrgSid,
                        Rank = rolesByGuild.TryGetValue(m.GuildId, out var roles)
                            ? DiscordRankResolver.Resolve(DiscordRoleLists.ParseRoleIds(m.RoleIdsJson), roles)?.Name
                            : null,
                    })
                    .ToList(),
                Links = linked.Select(p => p.ToDto()).ToList(),
                RsiOrgs = rosters
                    .Select(roster => linked.SelectMany(p => roster.Match(p)).FirstOrDefault())
                    .OfType<RosterRow>()
                    .OrderBy(r => r.OrgSid, StringComparer.OrdinalIgnoreCase)
                    .Select(r => new DiscordRsiOrgDto { Sid = r.OrgSid, Rank = r.Rank })
                    .ToList(),
            };
        }).ToList();

        return PaginatedResponse<DiscordMultiMemberDto>.Create(items, page, pageSize, total);
    }

    private static Dictionary<string, MemberReconciliation> Reconcile(
        string? orgSid,
        IEnumerable<ReconciliationSubject> subjects,
        IReadOnlyDictionary<string, List<LinkedPerson>> links,
        RosterIndex? roster)
    {
        var result = new Dictionary<string, MemberReconciliation>(StringComparer.Ordinal);
        foreach (var subject in subjects)
        {
            var persons = links.TryGetValue(subject.DiscordUserId, out var linked) ? linked : NoPersons;
            var dtos = persons.Select(p => p.ToDto()).ToList();
            var multiple = persons.Count > 1;

            string? status;
            string? rsiRank = null;
            var about = persons.FirstOrDefault();
            if (orgSid is null || subject.IsBot)
            {
                status = null;
                about = null;
            }
            else if (roster is null)
            {
                status = DiscordReconciliationStatus.RsiUnknown;
            }
            else if (persons.Count == 0)
            {
                status = DiscordReconciliationStatus.Unlinked;
            }
            else
            {
                var matches = persons.SelectMany(p => roster.Match(p).Select(row => (Person: p, Row: row))).ToList();
                if (matches.Count == 0)
                {
                    status = DiscordReconciliationStatus.NotInRsiOrg;
                }
                else
                {
                    var coherent = matches.FirstOrDefault(x => RankIsCoherent(subject.RsiRankLabel, x.Row.Rank));
                    var chosen = coherent.Row is not null ? coherent : matches[0];
                    status = coherent.Row is not null ? DiscordReconciliationStatus.Ok : DiscordReconciliationStatus.RankMismatch;
                    rsiRank = chosen.Row.Rank;
                    about = chosen.Person;
                }
            }
            result[subject.DiscordUserId] = new MemberReconciliation(status, multiple, dtos, rsiRank, about?.ToDto());
        }
        return result;
    }

    /// <summary>The people linked to each Discord id (distinct entities, oldest first).</summary>
    private async Task<Dictionary<string, List<LinkedPerson>>> LoadLinksAsync(
        IEnumerable<string> discordUserIds, CancellationToken ct)
    {
        var ids = discordUserIds.Distinct(StringComparer.Ordinal).ToList();
        if (ids.Count == 0) return new Dictionary<string, List<LinkedPerson>>(StringComparer.Ordinal);

        var rows = await (
                from l in db.EntityLinks.AsNoTracking()
                join e in db.TrackedEntities.AsNoTracking() on l.TrackedEntityId equals e.Id
                where l.Provider == LinkProviders.Discord && ids.Contains(l.Value)
                select new { l.Value, EntityId = e.Id, e.CitizenId, e.CurrentHandle, e.DisplayName })
            .ToListAsync(ct);
        return rows
            .GroupBy(r => r.Value, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.DistinctBy(r => r.EntityId)
                    .OrderBy(r => r.EntityId)
                    .Select(r => new LinkedPerson(r.EntityId, r.CitizenId, r.CurrentHandle, r.DisplayName))
                    .ToList(),
                StringComparer.Ordinal);
    }

    /// <summary>Active roster rows of the org that may be one of <paramref name="persons"/>.</summary>
    private async Task<List<RosterRow>> LoadRosterOfAsync(
        string orgSid, IReadOnlyCollection<LinkedPerson> persons, CancellationToken ct)
    {
        var citizenIds = persons.Where(p => p.CitizenId is not null).Select(p => p.CitizenId!.Value).Distinct().ToList();
        var handles = persons.Where(p => p.Handle is not null).Select(p => p.Handle!)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (citizenIds.Count == 0 && handles.Count == 0) return [];

        return await db.OrganizationMembers.AsNoTracking()
            .Where(m => m.OrgSid == orgSid && m.IsActive
                && ((m.CitizenId != null && citizenIds.Contains(m.CitizenId.Value))
                    || handles.Contains(EF.Functions.Collate(m.UserHandle, "NOCASE"))))
            .Select(m => new RosterRow(m.OrgSid, m.UserHandle, m.CitizenId, m.Rank))
            .ToListAsync(ct);
    }

    /// <summary>
    /// Active roster rows, in any org, that may be one of <paramref name="persons"/>: by citizen
    /// id, then by handle through the NOCASE index (two queries, each served by an index).
    /// </summary>
    private async Task<List<RosterRow>> LoadActiveOrgRowsAsync(IReadOnlyCollection<LinkedPerson> persons, CancellationToken ct)
    {
        var citizenIds = persons.Where(p => p.CitizenId is not null).Select(p => p.CitizenId!.Value).Distinct().ToList();
        var handles = persons.Where(p => p.Handle is not null).Select(p => p.Handle!)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var rows = new List<RosterRow>();
        if (citizenIds.Count > 0)
        {
            rows.AddRange(await db.OrganizationMembers.AsNoTracking()
                .Where(m => m.IsActive && m.CitizenId != null && citizenIds.Contains(m.CitizenId.Value))
                .Select(m => new RosterRow(m.OrgSid, m.UserHandle, m.CitizenId, m.Rank))
                .ToListAsync(ct));
        }
        if (handles.Count > 0)
        {
            rows.AddRange(await db.OrganizationMembers.AsNoTracking()
                .Where(m => m.IsActive && handles.Contains(EF.Functions.Collate(m.UserHandle, "NOCASE")))
                .Select(m => new RosterRow(m.OrgSid, m.UserHandle, m.CitizenId, m.Rank))
                .ToListAsync(ct));
        }
        return rows.Distinct().ToList();
    }

    private async Task<Dictionary<string, DiscordRole>> LoadRolesAsync(string guildId, CancellationToken ct)
        => (await db.DiscordRoles.AsNoTracking().Where(r => r.GuildId == guildId).ToListAsync(ct))
            .ToDictionary(r => r.RoleId, StringComparer.Ordinal);

    private static string PersonKey(RosterRow row)
        => row.CitizenId is int citizenId
            ? citizenId.ToString(CultureInfo.InvariantCulture)
            : "h:" + row.Handle.ToLowerInvariant();

    private static int KindOrder(string kind) => kind switch
    {
        DiscordDiscrepancyKinds.NotInRsiOrg => 0,
        DiscordDiscrepancyKinds.RankMismatch => 1,
        _ => 2,
    };

    /// <summary>A tracked person behind a discord link.</summary>
    private sealed record LinkedPerson(long EntityId, int? CitizenId, string? Handle, string? DisplayName)
    {
        public DiscordLinkedPersonDto ToDto() => new() { Handle = Handle, CitizenId = CitizenId, DisplayName = DisplayName };
    }

    /// <summary>An active organization_members row, reduced to what reconciliation reads.</summary>
    private sealed record RosterRow(string OrgSid, string Handle, int? CitizenId, string? Rank);

    /// <summary>Active roster rows of one org, by citizen id and by handle regardless of case.</summary>
    private sealed class RosterIndex
    {
        private readonly Dictionary<int, List<RosterRow>> _byCitizen = new();
        private readonly Dictionary<string, List<RosterRow>> _byHandle = new(StringComparer.OrdinalIgnoreCase);

        public RosterIndex(IEnumerable<RosterRow> rows)
        {
            Rows = rows.ToList();
            foreach (var row in Rows)
            {
                if (row.CitizenId is int citizenId) Add(_byCitizen, citizenId, row);
                Add(_byHandle, row.Handle, row);
            }
        }

        public IReadOnlyList<RosterRow> Rows { get; }

        /// <summary>
        /// The rows of <paramref name="person"/>: by citizen id first; else by handle regardless
        /// of case, except rows of another known citizen (the handle was taken again).
        /// </summary>
        public IReadOnlyList<RosterRow> Match(LinkedPerson person)
        {
            if (person.CitizenId is int citizenId && _byCitizen.TryGetValue(citizenId, out var byCitizen)) return byCitizen;
            if (person.Handle is null || !_byHandle.TryGetValue(person.Handle, out var byHandle)) return [];
            return byHandle
                .Where(r => r.CitizenId is null || person.CitizenId is null || r.CitizenId == person.CitizenId)
                .ToList();
        }

        private static void Add<TKey>(Dictionary<TKey, List<RosterRow>> index, TKey key, RosterRow row) where TKey : notnull
        {
            if (!index.TryGetValue(key, out var rows))
            {
                rows = [];
                index[key] = rows;
            }
            rows.Add(row);
        }
    }
}
```

- [ ] **Step 5: Add the controller and register the service**

Create `src/Collector.Api/Controllers/DiscordRostersController.cs`:

```csharp
using Collector.Api.Dtos.Common;
using Collector.Api.Dtos.Discord;
using Collector.Api.Services.Discord;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Collector.Api.Controllers;

/// <summary>
/// Reads and edits over the Discord rosters sent by the Vencord plugin (spec § 11). Every
/// route sits under the default policy (Smart scheme, signed-in user), so a discord:ingest
/// key is refused here. Sub-paths are complete, as in DiscordController, because the routes
/// span api/discord, api/users and api/organizations. Actions take their services with
/// [FromServices]: the routes are spread over several services.
/// </summary>
[ApiController]
[Route("api")]
[Authorize]
public class DiscordRostersController : ControllerBase
{
    /// <summary>RSI discrepancies and totals of a guild (spec § 10.2).</summary>
    [HttpGet("discord/guilds/{guildId}/discrepancies")]
    public async Task<ActionResult<DiscordDiscrepanciesDto>> GetDiscrepancies(
        string guildId, [FromServices] DiscordReconciliationService reconciliation, CancellationToken ct)
        => Ok(await reconciliation.GetDiscrepanciesAsync(guildId, ct));

    /// <summary>Accounts active in at least two tracked guilds (spec § 10.3).</summary>
    [HttpGet("discord/multi")]
    public async Task<ActionResult<PaginatedResponse<DiscordMultiMemberDto>>> GetMultiMembership(
        [FromServices] DiscordReconciliationService reconciliation,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
        => Ok(await reconciliation.GetMultiMembershipAsync(page, pageSize, ct));
}
```

In `src/Collector.Api/Extensions/ServiceCollectionExtensions.cs`, method `AddApiServices`, replace:

```csharp
        services.AddHostedService<AudioOrphanSweeper>();
```

with:

```csharp
        services.AddHostedService<AudioOrphanSweeper>();

        // Discord reconciliation with RSI rosters, discrepancies and multi-membership (spec § 10.2–10.3).
        services.AddScoped<Collector.Api.Services.Discord.DiscordReconciliationService>();
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test src/Collector.Api.Tests --filter "FullyQualifiedName~DiscordReconciliationTests"`

Expected: `Passed!  - Failed:     0, Passed:     9, Skipped:     0, Total:     9`

- [ ] **Step 7: Check that the new routes stay closed to anonymous callers and scoped keys**

Run: `dotnet test src/Collector.Api.Tests --filter "FullyQualifiedName~AuthorizationTests"`

Expected: `Passed!` with `Failed:     0` (`GET /api/discord/guilds/x/discrepancies` and `GET /api/discord/multi` answer 401 to an anonymous caller and to a `discord:ingest` key).

- [ ] **Step 8: Commit**

```bash
git add src/Collector.Api/Dtos/Discord/DiscordRosterDtos.cs \
        src/Collector.Api/Services/Discord/DiscordReconciliationService.cs \
        src/Collector.Api/Controllers/DiscordRostersController.cs \
        src/Collector.Api/Extensions/ServiceCollectionExtensions.cs \
        src/Collector.Api.Tests/Discord/DiscordReadSeed.cs \
        src/Collector.Api.Tests/Discord/DiscordReconciliationTests.cs
git commit -F - <<'EOF'
feat(api): reconcile discord members with rsi rosters and list multi-guild accounts

Linking Discord accounts to citizens only pays off once the tracker says
who is where: a linked member missing from the RSI roster, a Discord rank
that disagrees with the RSI one, or a roster member absent from the
server. Statuses are computed on read from the guild's current mapping, so
a guild re-mapped to another corpo is reconciled against the new roster at
once. A person is matched by citizen id first and only then by handle,
never on a handle another citizen holds now. Absences on the RSI side are
listed only after a complete sync, the only one that proves them, and the
RSI totals come from the latest counters alone, never an older breakdown.
Multi-membership lists the accounts active in several tracked guilds with
the RSI orgs of the people behind them.

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
EOF
```

### Task C3: Guild, members, events and syncs reads

**Files:**
- Create: `src/Collector.Api/Services/Discord/DiscordRosterQueryService.cs`
- Modify: `src/Collector.Api/Dtos/Discord/DiscordRosterDtos.cs` (after the last class, `DiscordMultiMemberDto`)
- Modify: `src/Collector.Api/Controllers/DiscordRostersController.cs` (usings; after the `GetMultiMembership` action)
- Modify: `src/Collector.Api/Extensions/ServiceCollectionExtensions.cs` (`AddApiServices`, after `services.AddHostedService<AudioOrphanSweeper>();`)
- Test: `src/Collector.Api.Tests/Discord/DiscordRosterQueryTests.cs`
- Test: `src/Collector.Api.Tests/Discord/DiscordRosterQueryCostTests.cs`

**Interfaces:**
- Consumes:
  - CONTRACTS § 1 (lot A): entities `DiscordGuild`, `DiscordRole`, `DiscordAccount`, `DiscordMember`, `DiscordMemberEvent`, `DiscordSync`, `DiscordEventTypes`, `DiscordSyncMethods` and their DbSets; the `AddDiscordRosters` migration (the cost test migrates an in-memory database).
  - CONTRACTS § 0 (lot A): `Collector.Discord.DiscordSnowflake.IsValid(string? s)`; § 5: `DiscordTestKit.NewSnowflake()`.
  - Task C1: `DiscordRankResolver.Resolve`, `DiscordRankResolver.Compare`, `RankRole`, `DiscordRoleLists.ParseRoleIds`, `DiscordRoleLists.ParseEventRoles`, `EventRole`.
  - Task C2: `DiscordReconciliationService.ReconcileAsync(string? orgSid, IReadOnlyCollection<ReconciliationSubject> subjects, CancellationToken ct)`, `DiscordReconciliationService.RsiRankLabelOf(RankRole?, IReadOnlyDictionary<string, DiscordRole>)`, `ReconciliationSubject`, `MemberReconciliation`, `DiscordReconciliationStatus.IsValid`, `DiscordLinkedPersonDto`; `DiscordRostersController` (non-partial, `[FromServices]` actions); test helper `DiscordReadSeed`.
  - Existing: `IOrganizationRepository.GetLatestNamesBySidsAsync(IReadOnlyCollection<string>, CancellationToken)`, `OrganizationRepository(TrackerDbContext)`, `CurrentUserAccessor` (`UserId`, `IsAdmin`), `Paging.Page/PageSize/Limit`, `PaginatedResponse<T>`, `NotFoundException`, `ValidationException` (400).
- Produces:
  - `Collector.Api.Services.Discord.DiscordRosterQueryService` (scoped), constructor `(TrackerDbContext db, DiscordReconciliationService reconciliation, IOrganizationRepository organizations, CurrentUserAccessor currentUser)`:
    - `Task<IReadOnlyList<DiscordGuildSummaryDto>> ListGuildsAsync(CancellationToken ct)`;
    - `Task<DiscordGuildDetailDto?> GetGuildAsync(string guildId, CancellationToken ct)` — null for an unknown guild; `Roles` lists every role, deleted ones included (C6 reads it back after its PUTs);
    - `Task<PaginatedResponse<DiscordMemberDto>> GetMembersAsync(string guildId, DiscordMemberQuery query, CancellationToken ct)`;
    - `Task<IReadOnlyList<DiscordEventDto>> GetEventsAsync(string guildId, string? type, string? userId, int limit, CancellationToken ct)`;
    - `Task<IReadOnlyList<DiscordSyncDto>> GetSyncsAsync(string guildId, int limit, CancellationToken ct)`;
    - `const int DefaultLimit = 100`.
  - `public sealed record DiscordMemberQuery(string? Status, string? Search, string? RankRoleId, string? Reconciliation, int Page, int PageSize)`.
  - DTOs appended to `DiscordRosterDtos.cs`: `DiscordRankDto`, `DiscordRankCountDto`, `DiscordLastSyncDto`, `DiscordGuildSummaryDto` (not sealed), `DiscordRoleDto`, `DiscordGuildDetailDto : DiscordGuildSummaryDto`, `DiscordMemberDto`, `DiscordRankChangeDto`, `DiscordEventDto`, `DiscordSyncDto` (CONTRACTS § 7 JSON).
  - Routes (CONTRACTS § 7): `GET api/discord/guilds`, `GET api/discord/guilds/{guildId}` (404), `GET api/discord/guilds/{guildId}/members` (400 bad `status`/`rankRoleId`/`reconciliation`, 404), `GET api/discord/guilds/{guildId}/events` (404), `GET api/discord/guilds/{guildId}/syncs` (404).
  - Decisions: members are sorted by `COALESCE(nick, globalName, username) COLLATE NOCASE`, then Discord id; search is `LIKE` (ASCII case-insensitive, wildcards escaped) on username, global name and nick; an invalid `reconciliation` value is 400 even on an unmapped guild, where a valid one is ignored; `rankDistribution` lists the live ranks holding at least one active human, in rank order; roles are listed live ones by position descending, then deleted ones; the guild's events include the account-level events (`GuildId` null: renames) of accounts that are or were members; `rankChange.from/to` carry the role names stored in the event (names at the time) for the ranks resolved with the current configuration, and is null when the rank did not change.

- [ ] **Step 1: Write the failing tests**

Create `src/Collector.Api.Tests/Discord/DiscordRosterQueryTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Collector.Models;
using FluentAssertions;
using Xunit;

namespace Collector.Api.Tests.Discord;

/// <summary>
/// GET api/discord/guilds, …/guilds/{guildId}, …/members, …/events and …/syncs (spec § 11):
/// guild summaries and detail, members with rank, links and status, the guild's history and
/// its sync log.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class DiscordRosterQueryTests(ApiFactory factory)
{
    private static int _seq = 73_000;
    private readonly DiscordReadSeed _seed = new(factory);

    private static DateTime At => DiscordReadSeed.At;

    private static int Next() => Interlocked.Increment(ref _seq);

    /// <summary>A citizen id no other test uses.</summary>
    private static int Cid(int n, int k) => 1_730_000_000 + n * 20 + k;

    [Fact]
    public async Task TheGuildList_PutsUnmappedGuildsFirst_ThenSortsByName_AndSummarisesEachGuild()
    {
        var n = Next();
        var sid = $"QGL{n}";
        await _seed.SeedOrgAsync(sid, "Corpo Q");
        var alpha = await _seed.SeedGuildAsync(sid, name: $"alpha {n}", mappedByApiUserId: 1, mappedByUsername: $"c3-owner-{n}");
        var mike = await _seed.SeedGuildAsync(null, name: $"Mike {n}");
        var bravo = await _seed.SeedGuildAsync(null, name: $"bravo {n}", complete: false);
        var zulu = await _seed.SeedGuildAsync(sid, name: $"Zulu {n}");
        var officier = await _seed.SeedRoleAsync(alpha, "Officier", 20, isRank: true, color: "#ff0000");
        var recrue = await _seed.SeedRoleAsync(alpha, "Recrue", 10, isRank: true, color: null);
        var pilote = await _seed.SeedRoleAsync(alpha, "Pilote", 30, isRank: false);
        await _seed.SeedMemberAsync(alpha, $"q-officer{n}", [officier, recrue, pilote]);
        await _seed.SeedMemberAsync(alpha, $"q-recruit{n}", [recrue]);
        await _seed.SeedMemberAsync(alpha, $"q-recruit2{n}", [recrue]);
        await _seed.SeedMemberAsync(alpha, $"q-plain{n}", [pilote]);
        await _seed.SeedMemberAsync(alpha, $"q-bot{n}", [officier], bot: true);
        await _seed.SeedMemberAsync(alpha, $"q-left{n}", [officier], left: true);
        await SeedSyncAsync(alpha, $"first-{n}", At.AddDays(-2));
        await SeedSyncAsync(alpha, $"last-{n}", At.AddDays(-1), isComplete: false, guard: true, method: DiscordSyncMethods.RoleMembers);
        var client = await factory.SignedInClientAsync($"c3-list-{n}");

        var guilds = (await GetOkAsync(client, "/api/discord/guilds")).EnumerateArray().ToList();

        var mine = new[] { alpha, mike, bravo, zulu };
        guilds.Select(g => g.GetProperty("guildId").GetString()).Where(id => mine.Contains(id))
            .Should().Equal(bravo, mike, alpha, zulu);
        var summary = guilds.Single(g => g.GetProperty("guildId").GetString() == alpha);
        summary.GetProperty("name").GetString().Should().Be($"alpha {n}");
        summary.GetProperty("orgSid").GetString().Should().Be(sid);
        summary.GetProperty("orgName").GetString().Should().Be("Corpo Q");
        summary.GetProperty("orgMappedBy").GetString().Should().Be($"c3-owner-{n}");
        summary.GetProperty("activeMembers").GetInt32().Should().Be(4, "bots and departed members are not counted");
        summary.GetProperty("rankDistribution").EnumerateArray()
            .Select(r => (r.GetProperty("roleId").GetString(), r.GetProperty("name").GetString(),
                r.GetProperty("color").GetString(), r.GetProperty("count").GetInt32()))
            .Should().Equal((officier, "Officier", "#ff0000", 1), (recrue, "Recrue", null, 2));
        var lastSync = summary.GetProperty("lastSync");
        lastSync.GetProperty("submittedBy").GetString().Should().Be($"last-{n}");
        lastSync.GetProperty("isComplete").GetBoolean().Should().BeFalse();
        lastSync.GetProperty("method").GetString().Should().Be("role-members");
        lastSync.GetProperty("departureGuardTripped").GetBoolean().Should().BeTrue();
        lastSync.GetProperty("receivedAt").GetDateTime().Should().Be(At.AddDays(-1));
        summary.GetProperty("lastCompleteSyncAt").GetDateTime().Should().Be(At);
        var bravoSummary = guilds.Single(g => g.GetProperty("guildId").GetString() == bravo);
        bravoSummary.GetProperty("lastSync").ValueKind.Should().Be(JsonValueKind.Null);
        bravoSummary.GetProperty("lastCompleteSyncAt").ValueKind.Should().Be(JsonValueKind.Null);
        bravoSummary.GetProperty("orgName").ValueKind.Should().Be(JsonValueKind.Null);
        bravoSummary.GetProperty("rankDistribution").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task AGuildsDetail_ListsEveryRoleWithItsMembers_TheOrgsRsiRanks_AndWhoMayEditIt()
    {
        var n = Next();
        var sid = $"QGD{n}";
        await _seed.SeedOrgAsync(sid, "Corpo D");
        await _seed.SeedRosterAsync(sid, $"R1{n}", Cid(n, 1), " Officer", stars: 4);
        await _seed.SeedRosterAsync(sid, $"R2{n}", Cid(n, 2), "officer ", stars: 4);
        await _seed.SeedRosterAsync(sid, $"R3{n}", Cid(n, 3), "Recruit", stars: 1);
        await _seed.SeedRosterAsync(sid, $"R4{n}", Cid(n, 4), null);
        await _seed.SeedRosterAsync(sid, $"R5{n}", Cid(n, 5), "Legacy", active: false, stars: 5);
        var owner = await factory.SignedInClientAsync($"c3-owner-{n}");
        var ownerId = (await GetOkAsync(owner, "/api/auth/me")).GetProperty("id").GetInt64();
        var mapped = await _seed.SeedGuildAsync(sid, mappedByApiUserId: ownerId, mappedByUsername: $"c3-owner-{n}");
        var officier = await _seed.SeedRoleAsync(mapped, "Officier", 20, isRank: true, rankOrder: 5, rsiRankLabel: "Officer");
        var recrue = await _seed.SeedRoleAsync(mapped, "Recrue", 10, isRank: true);
        var pilote = await _seed.SeedRoleAsync(mapped, "Pilote", 30, isRank: false, color: null);
        var ancien = await _seed.SeedRoleAsync(mapped, "Ancien", 40, isRank: true, deleted: true);
        await _seed.SeedMemberAsync(mapped, $"d-one{n}", [officier, pilote]);
        await _seed.SeedMemberAsync(mapped, $"d-two{n}", [recrue, pilote, ancien]);
        await _seed.SeedMemberAsync(mapped, $"d-bot{n}", [pilote], bot: true);
        await _seed.SeedMemberAsync(mapped, $"d-left{n}", [officier], left: true);
        var unmapped = await _seed.SeedGuildAsync(null);
        var other = await factory.SignedInClientAsync($"c3-other-{n}");
        var admin = await factory.SignedInClientAsync($"c3-admin-{n}", isAdmin: true);
        var url = $"/api/discord/guilds/{mapped}";

        var detail = await GetOkAsync(other, url);

        detail.GetProperty("roles").EnumerateArray()
            .Select(r => (r.GetProperty("roleId").GetString(), r.GetProperty("name").GetString(), r.GetProperty("position").GetInt32(),
                r.GetProperty("isRank").GetBoolean(), Int(r.GetProperty("rankOrder")), r.GetProperty("rsiRankLabel").GetString(),
                r.GetProperty("deleted").GetBoolean(), r.GetProperty("memberCount").GetInt32()))
            .Should().Equal(
                (pilote, "Pilote", 30, false, null, null, false, 2),
                (officier, "Officier", 20, true, 5, "Officer", false, 1),
                (recrue, "Recrue", 10, true, 10, null, false, 1),
                (ancien, "Ancien", 40, true, 40, null, true, 1));
        // RankOrder decides before Position: Recrue (10) ranks above Officier (5).
        detail.GetProperty("rankDistribution").EnumerateArray().Select(r => r.GetProperty("roleId").GetString())
            .Should().Equal(recrue, officier);
        detail.GetProperty("rsiRanks").EnumerateArray().Select(r => r.GetString()).Should().Equal("Officer", "Recruit");
        detail.GetProperty("orgName").GetString().Should().Be("Corpo D");
        detail.GetProperty("canEdit").GetBoolean().Should().BeFalse("another user mapped this guild");
        (await GetOkAsync(owner, url)).GetProperty("canEdit").GetBoolean().Should().BeTrue();
        (await GetOkAsync(admin, url)).GetProperty("canEdit").GetBoolean().Should().BeTrue();
        var unmappedDetail = await GetOkAsync(other, $"/api/discord/guilds/{unmapped}");
        unmappedDetail.GetProperty("canEdit").GetBoolean().Should().BeTrue("anyone may configure an unmapped guild");
        unmappedDetail.GetProperty("rsiRanks").GetArrayLength().Should().Be(0);
        unmappedDetail.GetProperty("roles").GetArrayLength().Should().Be(0);
        (await other.GetAsync($"/api/discord/guilds/{DiscordTestKit.NewSnowflake()}")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Members_AreFilteredByStatusAndName_AndPagedInNameOrder()
    {
        var n = Next();
        var guildId = await _seed.SeedGuildAsync(null);
        var anna = await _seed.SeedMemberAsync(guildId, $"anna{n}", nick: "Zed");
        var bob = await _seed.SeedMemberAsync(guildId, $"bob{n}", globalName: "Bobby");
        await _seed.SeedMemberAsync(guildId, $"carl{n}");
        var dora = await _seed.SeedMemberAsync(guildId, $"dora{n}", left: true);
        var echo = await _seed.SeedMemberAsync(guildId, $"echo{n}", bot: true);
        var client = await factory.SignedInClientAsync($"c3-members-{n}");
        var url = $"/api/discord/guilds/{guildId}/members";

        var active = await GetOkAsync(client, url);

        // Sorted by nick, else global name, else username, regardless of case.
        Usernames(active).Should().Equal($"bob{n}", $"carl{n}", $"echo{n}", $"anna{n}");
        active.GetProperty("total").GetInt32().Should().Be(4);
        Items(active).Should().OnlyContain(m => m.GetProperty("reconciliation").ValueKind == JsonValueKind.Null);
        Items(active).Single(m => m.GetProperty("discordUserId").GetString() == echo).GetProperty("isBot").GetBoolean().Should().BeTrue();
        Items(active).Single(m => m.GetProperty("discordUserId").GetString() == bob).GetProperty("globalName").GetString().Should().Be("Bobby");
        Items(active).Single(m => m.GetProperty("discordUserId").GetString() == anna).GetProperty("nick").GetString().Should().Be("Zed");

        var second = await GetOkAsync(client, $"{url}?status=active&page=2&pageSize=2");
        Usernames(second).Should().Equal($"echo{n}", $"anna{n}");
        second.GetProperty("total").GetInt32().Should().Be(4);
        second.GetProperty("page").GetInt32().Should().Be(2);
        second.GetProperty("pageSize").GetInt32().Should().Be(2);

        var former = await GetOkAsync(client, $"{url}?status=former");
        Usernames(former).Should().Equal($"dora{n}");
        Items(former).Single().GetProperty("leftAt").GetDateTime().Should().Be(At);
        Usernames(await GetOkAsync(client, $"{url}?status=all"))
            .Should().Equal($"bob{n}", $"carl{n}", $"dora{n}", $"echo{n}", $"anna{n}");
        Items(former).Single().GetProperty("discordUserId").GetString().Should().Be(dora);

        Usernames(await GetOkAsync(client, $"{url}?search=BOBBY")).Should().Equal($"bob{n}");
        Usernames(await GetOkAsync(client, $"{url}?search=zed")).Should().Equal($"anna{n}");
        Usernames(await GetOkAsync(client, $"{url}?search=CARL{n}")).Should().Equal($"carl{n}");
        Usernames(await GetOkAsync(client, $"{url}?status=all&search=dora")).Should().Equal($"dora{n}");
        (await GetOkAsync(client, $"{url}?search=%25")).GetProperty("total").GetInt32().Should().Be(0, "wildcards are escaped");

        // An unmapped guild has no status: the filter is ignored.
        Usernames(await GetOkAsync(client, $"{url}?reconciliation=ok")).Should().HaveCount(4);

        (await client.GetAsync($"{url}?status=everyone")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.GetAsync($"/api/discord/guilds/{DiscordTestKit.NewSnowflake()}/members")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Members_CarryTheirRankRolesLinksAndStatus_AndAreFilteredByRankAndStatus()
    {
        var n = Next();
        var sid = $"QGM{n}";
        await _seed.SeedOrgAsync(sid, $"Org {sid}");
        await _seed.SeedRosterAsync(sid, $"Ok{n}", Cid(n, 1), "Officer");
        await _seed.SeedRosterAsync(sid, $"Low{n}", Cid(n, 2), "Recruit");
        var guildId = await _seed.SeedGuildAsync(sid);
        var officier = await _seed.SeedRoleAsync(guildId, "Officier", 20, isRank: true, rsiRankLabel: "Officer", color: "#ff0000");
        var recrue = await _seed.SeedRoleAsync(guildId, "Recrue", 10, isRank: true, rsiRankLabel: "Recruit", color: null);
        var pilote = await _seed.SeedRoleAsync(guildId, "Pilote", 30, isRank: false, color: null);
        var ok = await _seed.SeedMemberAsync(guildId, $"m-ok{n}", [recrue, officier, pilote]);
        var mismatch = await _seed.SeedMemberAsync(guildId, $"m-mismatch{n}", [officier]);
        var outsider = await _seed.SeedMemberAsync(guildId, $"m-outsider{n}", [recrue]);
        var unlinked = await _seed.SeedMemberAsync(guildId, $"m-unlinked{n}", [recrue]);
        var bot = await _seed.SeedMemberAsync(guildId, $"m-zbot{n}", [officier], bot: true);
        await _seed.SeedPersonAsync(Cid(n, 1), $"Ok{n}", "Okay", ok);
        await _seed.SeedPersonAsync(Cid(n, 2), $"Low{n}", null, mismatch);
        await _seed.SeedPersonAsync(Cid(n, 3), $"Out{n}", null, outsider);
        await _seed.SeedPersonAsync(Cid(n, 4), $"OutToo{n}", null, outsider);
        var client = await factory.SignedInClientAsync($"c3-status-{n}");
        var url = $"/api/discord/guilds/{guildId}/members";

        var all = await GetOkAsync(client, url);

        Usernames(all).Should().Equal($"m-mismatch{n}", $"m-ok{n}", $"m-outsider{n}", $"m-unlinked{n}", $"m-zbot{n}");
        var byId = Items(all).ToDictionary(m => m.GetProperty("discordUserId").GetString()!);
        var okItem = byId[ok];
        okItem.GetProperty("rank").GetProperty("roleId").GetString().Should().Be(officier);
        okItem.GetProperty("rank").GetProperty("name").GetString().Should().Be("Officier");
        okItem.GetProperty("rank").GetProperty("color").GetString().Should().Be("#ff0000");
        okItem.GetProperty("roles").EnumerateArray().Select(r => r.GetProperty("roleId").GetString())
            .Should().Equal(pilote, officier, recrue);
        okItem.GetProperty("links").EnumerateArray()
            .Select(l => (l.GetProperty("handle").GetString(), l.GetProperty("citizenId").GetInt32(), l.GetProperty("displayName").GetString()))
            .Should().Equal(($"Ok{n}", Cid(n, 1), "Okay"));
        okItem.GetProperty("rsiRank").GetString().Should().Be("Officer");
        okItem.GetProperty("reconciliation").GetString().Should().Be("ok");
        okItem.GetProperty("multipleLinks").GetBoolean().Should().BeFalse();
        byId[mismatch].GetProperty("reconciliation").GetString().Should().Be("rank_mismatch");
        byId[mismatch].GetProperty("rsiRank").GetString().Should().Be("Recruit");
        byId[outsider].GetProperty("reconciliation").GetString().Should().Be("not_in_rsi_org");
        byId[outsider].GetProperty("multipleLinks").GetBoolean().Should().BeTrue();
        byId[outsider].GetProperty("links").GetArrayLength().Should().Be(2);
        byId[outsider].GetProperty("rank").GetProperty("roleId").GetString().Should().Be(recrue);
        byId[unlinked].GetProperty("reconciliation").GetString().Should().Be("unlinked");
        byId[unlinked].GetProperty("links").GetArrayLength().Should().Be(0);
        byId[bot].GetProperty("reconciliation").ValueKind.Should().Be(JsonValueKind.Null);
        byId[bot].GetProperty("rank").GetProperty("roleId").GetString().Should().Be(officier);

        Usernames(await GetOkAsync(client, $"{url}?reconciliation=not_in_rsi_org")).Should().Equal($"m-outsider{n}");
        Usernames(await GetOkAsync(client, $"{url}?reconciliation=unlinked")).Should().Equal($"m-unlinked{n}");
        // m-ok holds Recrue too, but their rank is Officier.
        var recruits = await GetOkAsync(client, $"{url}?rankRoleId={recrue}");
        Usernames(recruits).Should().Equal($"m-outsider{n}", $"m-unlinked{n}");
        recruits.GetProperty("total").GetInt32().Should().Be(2);
        var secondRecruit = await GetOkAsync(client, $"{url}?rankRoleId={recrue}&page=2&pageSize=1");
        Usernames(secondRecruit).Should().Equal($"m-unlinked{n}");
        secondRecruit.GetProperty("total").GetInt32().Should().Be(2);
        Usernames(await GetOkAsync(client, $"{url}?rankRoleId={officier}&reconciliation=ok")).Should().Equal($"m-ok{n}");
        Usernames(await GetOkAsync(client, $"{url}?rankRoleId={pilote}")).Should().BeEmpty("Pilote is not a rank");
        (await client.GetAsync($"{url}?reconciliation=everything")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.GetAsync($"{url}?rankRoleId=officier")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Events_AreNewestFirst_WithTheirSender_AndTheRankChangeOfARoleChange()
    {
        var n = Next();
        var guildId = await _seed.SeedGuildAsync(null);
        var otherGuild = await _seed.SeedGuildAsync(null);
        var officier = await _seed.SeedRoleAsync(guildId, "Officier", 20, isRank: true);
        var recrue = await _seed.SeedRoleAsync(guildId, "Recrue", 10, isRank: true);
        var pilote = await _seed.SeedRoleAsync(guildId, "Pilote", 30, isRank: false);
        var member = await _seed.SeedMemberAsync(guildId, $"ev-member{n}");
        var other = await _seed.SeedMemberAsync(guildId, $"ev-other{n}");
        var outsider = await _seed.SeedMemberAsync(otherGuild, $"ev-outsider{n}");
        var first = await SeedSyncAsync(guildId, $"alice{n}", At.AddDays(-3));
        var second = await SeedSyncAsync(guildId, $"bob{n}", At.AddDays(-2));
        var joined = await SeedEventAsync(guildId, member, first, DiscordEventTypes.Joined, null, "2026-08-29T12:00:00Z",
            occurredAt: At.AddDays(-3));
        var promoted = await SeedEventAsync(guildId, member, second, DiscordEventTypes.RolesChanged,
            RoleList((recrue, "Recrue"), (pilote, "Pilote")), RoleList((officier, "Officier de bord"), (pilote, "Pilote")),
            notBefore: At.AddDays(-3));
        var flair = await SeedEventAsync(guildId, other, second, DiscordEventTypes.RolesChanged,
            RoleList((pilote, "Pilote")), "[]", notBefore: At.AddDays(-3));
        var renamed = await SeedEventAsync(null, member, second, DiscordEventTypes.UsernameChanged, $"old{n}", $"ev-member{n}");
        await SeedEventAsync(otherGuild, outsider, second, DiscordEventTypes.Joined, null, null);
        await SeedEventAsync(null, outsider, second, DiscordEventTypes.UsernameChanged, $"x{n}", $"ev-outsider{n}");
        var purged = await SeedEventAsync(guildId, other, 999_999_999, DiscordEventTypes.NickChanged, null, "Pseudo");
        var client = await factory.SignedInClientAsync($"c3-events-{n}");
        var url = $"/api/discord/guilds/{guildId}/events";

        var events = (await GetOkAsync(client, url)).EnumerateArray().ToList();

        events.Select(e => e.GetProperty("id").GetInt64()).Should().Equal(purged, renamed, flair, promoted, joined);
        var byId = events.ToDictionary(e => e.GetProperty("id").GetInt64());
        var rankChange = byId[promoted].GetProperty("rankChange");
        rankChange.GetProperty("from").GetString().Should().Be("Recrue");
        rankChange.GetProperty("to").GetString().Should().Be("Officier de bord", "the rank is named as it was then");
        byId[promoted].GetProperty("submittedBy").GetString().Should().Be($"bob{n}");
        byId[promoted].GetProperty("username").GetString().Should().Be($"ev-member{n}");
        byId[promoted].GetProperty("notBefore").GetDateTime().Should().Be(At.AddDays(-3));
        byId[flair].GetProperty("rankChange").ValueKind.Should().Be(JsonValueKind.Null, "the rank did not change");
        byId[joined].GetProperty("rankChange").ValueKind.Should().Be(JsonValueKind.Null);
        byId[joined].GetProperty("submittedBy").GetString().Should().Be($"alice{n}");
        byId[joined].GetProperty("occurredAt").GetDateTime().Should().Be(At.AddDays(-3));
        byId[joined].GetProperty("type").GetString().Should().Be("joined");
        byId[renamed].GetProperty("guildId").ValueKind.Should().Be(JsonValueKind.Null);
        byId[renamed].GetProperty("oldValue").GetString().Should().Be($"old{n}");
        byId[purged].GetProperty("submittedBy").ValueKind.Should().Be(JsonValueKind.Null, "its sync was purged");

        Ids(await GetOkAsync(client, $"{url}?type=roles_changed")).Should().Equal(flair, promoted);
        Ids(await GetOkAsync(client, $"{url}?userId={member}")).Should().Equal(renamed, promoted, joined);
        Ids(await GetOkAsync(client, $"{url}?limit=2")).Should().Equal(purged, renamed);
        (await client.GetAsync($"/api/discord/guilds/{DiscordTestKit.NewSnowflake()}/events")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Syncs_AreNewestFirst_AndLimited()
    {
        var n = Next();
        var guildId = await _seed.SeedGuildAsync(null);
        var first = await SeedSyncAsync(guildId, $"a{n}", At.AddDays(-3));
        var guarded = await SeedSyncAsync(guildId, $"b{n}", At.AddDays(-2), isComplete: false, guard: true, method: DiscordSyncMethods.Cache);
        var last = await SeedSyncAsync(guildId, $"c{n}", At.AddDays(-1));
        var client = await factory.SignedInClientAsync($"c3-syncs-{n}");
        var url = $"/api/discord/guilds/{guildId}/syncs";

        var syncs = (await GetOkAsync(client, url)).EnumerateArray().ToList();

        syncs.Select(s => s.GetProperty("id").GetInt64()).Should().Equal(last, guarded, first);
        var middle = syncs[1];
        middle.GetProperty("submittedBy").GetString().Should().Be($"b{n}");
        middle.GetProperty("method").GetString().Should().Be("cache");
        middle.GetProperty("isComplete").GetBoolean().Should().BeFalse();
        middle.GetProperty("declaredComplete").GetBoolean().Should().BeFalse();
        middle.GetProperty("departureGuardTripped").GetBoolean().Should().BeTrue();
        middle.GetProperty("receivedAt").GetDateTime().Should().Be(At.AddDays(-2));
        middle.GetProperty("collectedAt").GetDateTime().Should().Be(At.AddDays(-2).AddMinutes(-5));
        middle.GetProperty("expectedCount").GetInt32().Should().Be(4);
        middle.GetProperty("collectedCount").GetInt32().Should().Be(4);
        middle.GetProperty("pluginVersion").GetString().Should().Be("1.0.0");
        Ids(await GetOkAsync(client, $"{url}?limit=2")).Should().Equal(last, guarded);
        (await client.GetAsync($"/api/discord/guilds/{DiscordTestKit.NewSnowflake()}/syncs")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
    }

    private static async Task<JsonElement> GetOkAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        response.StatusCode.Should().Be(HttpStatusCode.OK, url);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static List<JsonElement> Items(JsonElement page) => page.GetProperty("items").EnumerateArray().ToList();

    private static List<string?> Usernames(JsonElement page)
        => Items(page).Select(m => m.GetProperty("username").GetString()).ToList();

    private static List<long> Ids(JsonElement array) => array.EnumerateArray().Select(e => e.GetProperty("id").GetInt64()).ToList();

    private static int? Int(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null : value.GetInt32();

    /// <summary>A roles_changed value as the ingest stores it: [{"id","name"}], ordered by id.</summary>
    private static string RoleList(params (string Id, string Name)[] roles)
        => JsonSerializer.Serialize(roles.OrderBy(r => r.Id, StringComparer.Ordinal).Select(r => new { id = r.Id, name = r.Name }));

    private Task<long> SeedSyncAsync(
        string guildId, string by, DateTime receivedAt, bool isComplete = true, bool guard = false,
        string method = DiscordSyncMethods.MemberSearch)
        => _seed.ReadAsync(async db =>
        {
            var sync = new DiscordSync
            {
                GuildId = guildId, SubmittedByApiUserId = 1, SubmittedByUsername = by,
                ReceivedAt = receivedAt, CollectedAt = receivedAt.AddMinutes(-5), DeclaredCollectedAt = receivedAt.AddMinutes(-5),
                Method = method, DeclaredComplete = isComplete, IsComplete = isComplete, IsBaseline = false,
                DepartureGuardTripped = guard, ExpectedCount = 4, CollectedCount = 4, OptedOutCount = 0,
                UnknownRoleRefCount = 0, EventCount = 0, PluginVersion = "1.0.0",
            };
            db.DiscordSyncs.Add(sync);
            await db.SaveChangesAsync();
            return sync.Id;
        });

    private Task<long> SeedEventAsync(
        string? guildId, string userId, long syncId, string type, string? oldValue, string? newValue,
        DateTime? occurredAt = null, DateTime? notBefore = null)
        => _seed.ReadAsync(async db =>
        {
            var row = new DiscordMemberEvent
            {
                GuildId = guildId, DiscordUserId = userId, SyncId = syncId, Type = type,
                OldValue = oldValue, NewValue = newValue, OccurredAt = occurredAt, NotBefore = notBefore, ObservedAt = At,
            };
            db.DiscordMemberEvents.Add(row);
            await db.SaveChangesAsync();
            return row.Id;
        });
}
```

Create `src/Collector.Api.Tests/Discord/DiscordRosterQueryCostTests.cs` (review focus 4, on the `MembershipQueriesTests` SQL-capturing pattern):

```csharp
using System.Collections.Concurrent;
using System.Data.Common;
using Collector.Api.Auth;
using Collector.Api.Services.Discord;
using Collector.Data;
using Collector.Data.Repositories;
using Collector.Models;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Collector.Api.Tests.Discord;

/// <summary>
/// Review focus 4: a 20,000-member guild. The guild list never looks a handle up (no
/// suggestion is computed), and a members page is cut by the database in a bounded number
/// of queries, rank and status filters included.
/// </summary>
public sealed class DiscordRosterQueryCostTests : IAsyncLifetime
{
    private const int MemberCount = 20_000;
    private const string GuildId = "400000000000000001";
    private const string OfficerRoleId = "400000000000000011";
    private const string PilotRoleId = "400000000000000012";
    private const string FlairRoleId = "400000000000000013";
    private const string LinkedUserId = "500000000000000007";
    private static readonly DateTime At = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

    // Member i has the id 500000000000000000 + i and the username "member" + i on 5 digits.
    private const string AccountsSql = """
        INSERT INTO discord_accounts (DiscordUserId, Username, GlobalName, IsBot, FirstSeenAt, LastSeenAt)
        WITH RECURSIVE seq(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM seq WHERE i < 20000)
        SELECT CAST(500000000000000000 + i AS TEXT), printf('member%05d', i), NULL, 0,
               '2026-09-01 12:00:00', '2026-09-01 12:00:00'
        FROM seq;
        """;

    // Every 4th member is an officer who also wears the pilot role; the others are pilots,
    // one in twenty with the flair role (not a rank).
    private const string MembersSql = """
        INSERT INTO discord_members (GuildId, DiscordUserId, Nick, RoleIdsJson, JoinedAt, FirstSeenAt, LastSeenAt, LeftAt)
        WITH RECURSIVE seq(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM seq WHERE i < 20000)
        SELECT '400000000000000001', CAST(500000000000000000 + i AS TEXT), NULL,
               CASE WHEN i % 4 = 0 THEN '["400000000000000011","400000000000000012"]'
                    WHEN i % 20 = 10 THEN '["400000000000000012","400000000000000013"]'
                    ELSE '["400000000000000012"]' END,
               NULL, '2026-09-01 12:00:00', '2026-09-01 12:00:00', NULL
        FROM seq;
        """;

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SqlCapture _sql = new();
    private TrackerDbContext _db = null!;

    public async Task InitializeAsync()
    {
        _connection.Open();
        _db = new TrackerDbContext(new DbContextOptionsBuilder<TrackerDbContext>()
            .UseSqlite(_connection).AddInterceptors(_sql).Options);
        await _db.Database.MigrateAsync();

        _db.Organizations.Add(new Organization { Sid = "BIG", Name = "Big corpo", Timestamp = At });
        // The roster is known (no rsi_unknown); one of its members is linked to member 7.
        _db.OrganizationMembers.AddRange(
            new OrganizationMember { OrgSid = "BIG", UserHandle = "Linked7", CitizenId = 900_007, Rank = "Pilot", Timestamp = At, IsActive = true },
            new OrganizationMember { OrgSid = "BIG", UserHandle = "RosterOnly", CitizenId = 900_008, Rank = "Pilot", Timestamp = At, IsActive = true });
        _db.DiscordGuilds.Add(new DiscordGuild
        {
            GuildId = GuildId, Name = "Big guild", OrgSid = "BIG", FirstSyncAt = At, LastSyncAt = At, LastCollectedAt = At,
            LastCompleteSyncAt = At, CreatedAt = At, UpdatedAt = At,
        });
        _db.DiscordRoles.AddRange(
            Role(OfficerRoleId, "Officier", 20, isRank: true),
            Role(PilotRoleId, "Pilote", 10, isRank: true),
            Role(FlairRoleId, "Flair", 30, isRank: false));
        _db.DiscordSyncs.Add(new DiscordSync
        {
            GuildId = GuildId, SubmittedByApiUserId = 1, SubmittedByUsername = "sender", ReceivedAt = At, CollectedAt = At,
            DeclaredCollectedAt = At, Method = DiscordSyncMethods.MemberSearch, DeclaredComplete = true, IsComplete = true,
            ExpectedCount = MemberCount, CollectedCount = MemberCount, PluginVersion = "1.0.0",
        });
        var linked = new TrackedEntity { CitizenId = 900_007, CurrentHandle = "Linked7", CreatedAt = At, UpdatedAt = At };
        _db.TrackedEntities.Add(linked);
        await _db.SaveChangesAsync();
        _db.EntityLinks.Add(new EntityLink
        {
            TrackedEntityId = linked.Id, Provider = LinkProviders.Discord, Value = LinkedUserId,
            AuthorApiUserId = 0, AuthorUsername = "seed", CreatedAt = At, UpdatedAt = At,
        });
        await _db.SaveChangesAsync();
        await _db.Database.ExecuteSqlRawAsync(AccountsSql);
        await _db.Database.ExecuteSqlRawAsync(MembersSql);
        _db.ChangeTracker.Clear();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        _connection.Dispose();
    }

    [Fact]
    public async Task TheGuildList_ReadsNoHandle_AndTakesAFixedNumberOfQueries()
    {
        _sql.Commands.Clear();

        var guilds = await Service().ListGuildsAsync(CancellationToken.None);

        var guild = guilds.Should().ContainSingle().Subject;
        guild.ActiveMembers.Should().Be(MemberCount);
        guild.RankDistribution.Select(r => (r.RoleId, r.Count)).Should().Equal((OfficerRoleId, 5_000), (PilotRoleId, 15_000));
        _sql.Commands.Should().HaveCountLessThanOrEqualTo(5);
        _sql.Commands.Should().NotContain(c =>
            c.Contains("\"users\"") || c.Contains("\"user_handle_history\"") || c.Contains("\"organization_members\"")
            || c.Contains("\"entity_links\"") || c.Contains("\"discord_link_rejections\""),
            "the guild list never computes link suggestions");
    }

    [Fact]
    public async Task AMembersPage_IsCutByTheDatabase()
    {
        _sql.Commands.Clear();

        var page = await Service().GetMembersAsync(
            GuildId, new DiscordMemberQuery("active", null, null, null, Page: 3, PageSize: 50), CancellationToken.None);

        page.Total.Should().Be(MemberCount);
        page.Page.Should().Be(3);
        page.PageSize.Should().Be(50);
        page.Items.Select(m => m.Username).Should().Equal(Enumerable.Range(101, 50).Select(i => $"member{i:D5}"));
        page.Items.Should().OnlyContain(m => m.Rank != null);
        _sql.Commands.Should().HaveCountLessThanOrEqualTo(8);
        _sql.Commands.Should().Contain(c => c.Contains("\"discord_members\"") && c.Contains("LIMIT"));
    }

    [Theory]
    [InlineData(OfficerRoleId, 5_000)]
    [InlineData(PilotRoleId, 15_000)]
    public async Task ARankFilter_IsOneBoundedPass(string rankRoleId, int expected)
    {
        _sql.Commands.Clear();

        var page = await Service().GetMembersAsync(
            GuildId, new DiscordMemberQuery("active", null, rankRoleId, null, Page: 2, PageSize: 50), CancellationToken.None);

        // Officers also wear the pilot role: the pilot filter keeps only members whose rank it is.
        page.Total.Should().Be(expected);
        page.Items.Should().HaveCount(50).And.OnlyContain(m => m.Rank!.RoleId == rankRoleId);
        _sql.Commands.Should().HaveCountLessThanOrEqualTo(8);
        _sql.Commands.Should().Contain(c => c.Contains("\"discord_members\"") && c.Contains("LIKE"));
    }

    [Fact]
    public async Task AReconciliationFilter_IsOneBoundedPass()
    {
        _sql.Commands.Clear();
        var ok = await Service().GetMembersAsync(
            GuildId, new DiscordMemberQuery("active", null, null, "ok", Page: 1, PageSize: 50), CancellationToken.None);
        var okQueries = _sql.Commands.Count;
        _sql.Commands.Clear();

        var unlinked = await Service().GetMembersAsync(
            GuildId, new DiscordMemberQuery("active", null, null, "unlinked", Page: 1, PageSize: 50), CancellationToken.None);

        ok.Total.Should().Be(1);
        var linked = ok.Items.Should().ContainSingle().Subject;
        linked.DiscordUserId.Should().Be(LinkedUserId);
        linked.Links.Should().ContainSingle().Which.Handle.Should().Be("Linked7");
        linked.RsiRank.Should().Be("Pilot");
        unlinked.Total.Should().Be(MemberCount - 1);
        unlinked.Items.Should().HaveCount(50).And.OnlyContain(m => m.Reconciliation == "unlinked");
        okQueries.Should().BeLessThanOrEqualTo(8);
        _sql.Commands.Should().HaveCountLessThanOrEqualTo(8);
    }

    private DiscordRosterQueryService Service() => new(
        _db,
        new DiscordReconciliationService(_db),
        new OrganizationRepository(_db),
        new CurrentUserAccessor(new HttpContextAccessor()));

    private static DiscordRole Role(string roleId, string name, int position, bool isRank) => new()
    {
        GuildId = GuildId, RoleId = roleId, Name = name, Position = position, Color = null,
        Hoist = isRank, Managed = false, IsRank = isRank, RankOrder = isRank ? position : null,
        FirstSeenAt = At, LastSeenAt = At,
    };

    private sealed class SqlCapture : DbCommandInterceptor
    {
        public ConcurrentQueue<string> Commands { get; } = new();

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Enqueue(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test src/Collector.Api.Tests --filter "FullyQualifiedName~DiscordRosterQuery"`

Expected: the build fails with `error CS0246: The type or namespace name 'DiscordRosterQueryService' could not be found` (also `DiscordMemberQuery`).

- [ ] **Step 3: Add the DTOs**

In `src/Collector.Api/Dtos/Discord/DiscordRosterDtos.cs`, replace the end of `DiscordMultiMemberDto`:

```csharp
    /// <summary>Union of the active RSI orgs of every linked person, with their rank there.</summary>
    public IReadOnlyList<DiscordRsiOrgDto> RsiOrgs { get; set; } = [];
}
```

with:

```csharp
    /// <summary>Union of the active RSI orgs of every linked person, with their rank there.</summary>
    public IReadOnlyList<DiscordRsiOrgDto> RsiOrgs { get; set; } = [];
}

/// <summary>A Discord role as shown next to a member: its id, current name and colour.</summary>
public sealed class DiscordRankDto
{
    public string RoleId { get; set; } = null!;
    public string Name { get; set; } = null!;
    public string? Color { get; set; }
}

/// <summary>Number of active human members whose rank is this role.</summary>
public sealed class DiscordRankCountDto
{
    public string RoleId { get; set; } = null!;
    public string Name { get; set; } = null!;
    public string? Color { get; set; }
    public int Count { get; set; }
}

/// <summary>The last accepted sync of a guild.</summary>
public sealed class DiscordLastSyncDto
{
    public DateTime ReceivedAt { get; set; }
    public bool IsComplete { get; set; }
    public string Method { get; set; } = null!;
    public string SubmittedBy { get; set; } = null!;
    public bool DepartureGuardTripped { get; set; }
}

/// <summary>A tracked guild, as listed in the DISCORD tab.</summary>
public class DiscordGuildSummaryDto
{
    public string GuildId { get; set; } = null!;
    public string Name { get; set; } = null!;
    public string? IconHash { get; set; }
    public string? OrgSid { get; set; }

    /// <summary>Latest name of the org, when it is known to the tracker.</summary>
    public string? OrgName { get; set; }

    /// <summary>The guild's responsible: the user who mapped it (spec § 11).</summary>
    public string? OrgMappedBy { get; set; }

    /// <summary>Active, non-bot members.</summary>
    public int ActiveMembers { get; set; }

    /// <summary>Live ranks held by at least one active human member, in rank order.</summary>
    public IReadOnlyList<DiscordRankCountDto> RankDistribution { get; set; } = [];

    public DiscordLastSyncDto? LastSync { get; set; }
    public DateTime? LastCompleteSyncAt { get; set; }
}

/// <summary>A role of a guild with its rank configuration.</summary>
public sealed class DiscordRoleDto
{
    public string RoleId { get; set; } = null!;
    public string Name { get; set; } = null!;
    public int Position { get; set; }
    public string? Color { get; set; }
    public bool Hoist { get; set; }
    public bool Managed { get; set; }
    public bool IsRank { get; set; }
    public int? RankOrder { get; set; }
    public string? RsiRankLabel { get; set; }

    /// <summary>The role was missing from a later sync.</summary>
    public bool Deleted { get; set; }

    /// <summary>Active, non-bot members holding the role.</summary>
    public int MemberCount { get; set; }
}

/// <summary>A guild with its roles, the RSI ranks of its org and whether the caller may configure it.</summary>
public sealed class DiscordGuildDetailDto : DiscordGuildSummaryDto
{
    /// <summary>Every role, deleted ones last.</summary>
    public IReadOnlyList<DiscordRoleDto> Roles { get; set; } = [];

    /// <summary>Distinct, trimmed ranks of the org's active roster, highest stars first.</summary>
    public IReadOnlyList<string> RsiRanks { get; set; } = [];

    /// <summary>Unmapped, or the caller is its responsible or an admin.</summary>
    public bool CanEdit { get; set; }
}

/// <summary>A member of a guild with rank, links and reconciliation status.</summary>
public sealed class DiscordMemberDto
{
    public string DiscordUserId { get; set; } = null!;
    public string Username { get; set; } = null!;
    public string? GlobalName { get; set; }
    public string? Nick { get; set; }
    public bool IsBot { get; set; }
    public DateTime? JoinedAt { get; set; }
    public DateTime FirstSeenAt { get; set; }
    public DateTime LastSeenAt { get; set; }
    public DateTime? LeftAt { get; set; }
    public DiscordRankDto? Rank { get; set; }

    /// <summary>Live roles of the member, by position descending.</summary>
    public IReadOnlyList<DiscordRankDto> Roles { get; set; } = [];

    public IReadOnlyList<DiscordLinkedPersonDto> Links { get; set; } = [];
    public string? RsiRank { get; set; }

    /// <summary><see cref="DiscordReconciliationStatus"/> value; null when the guild is unmapped or the member is a bot.</summary>
    public string? Reconciliation { get; set; }

    public bool MultipleLinks { get; set; }
}

/// <summary>"Rang : X → Y" of a roles_changed event that moved the member's rank.</summary>
public sealed class DiscordRankChangeDto
{
    public string? From { get; set; }
    public string? To { get; set; }
}

/// <summary>One event of a guild's history.</summary>
public sealed class DiscordEventDto
{
    public long Id { get; set; }

    /// <summary>Null for an account-level event (username or global name change).</summary>
    public string? GuildId { get; set; }

    public string DiscordUserId { get; set; } = null!;

    /// <summary>The account's current username.</summary>
    public string? Username { get; set; }

    public string Type { get; set; } = null!;
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    public DateTime? OccurredAt { get; set; }
    public DateTime? NotBefore { get; set; }
    public DateTime ObservedAt { get; set; }

    /// <summary>Who sent the source sync; null once that sync left the log.</summary>
    public string? SubmittedBy { get; set; }

    public DiscordRankChangeDto? RankChange { get; set; }
}

/// <summary>One accepted sync of a guild.</summary>
public sealed class DiscordSyncDto
{
    public long Id { get; set; }
    public DateTime ReceivedAt { get; set; }
    public DateTime CollectedAt { get; set; }
    public string SubmittedBy { get; set; } = null!;
    public string Method { get; set; } = null!;
    public bool DeclaredComplete { get; set; }
    public bool IsComplete { get; set; }
    public bool IsBaseline { get; set; }
    public bool DepartureGuardTripped { get; set; }
    public int? ExpectedCount { get; set; }
    public int CollectedCount { get; set; }
    public int OptedOutCount { get; set; }
    public int UnknownRoleRefCount { get; set; }
    public int EventCount { get; set; }
    public string PluginVersion { get; set; } = null!;
}
```

- [ ] **Step 4: Write the query service**

Create `src/Collector.Api/Services/Discord/DiscordRosterQueryService.cs`:

```csharp
using Collector.Api.Auth;
using Collector.Api.Dtos.Common;
using Collector.Api.Dtos.Discord;
using Collector.Api.Errors;
using Collector.Api.Extensions;
using Collector.Data;
using Collector.Data.Repositories;
using Collector.Discord;
using Collector.Models;
using Microsoft.EntityFrameworkCore;

namespace Collector.Api.Services.Discord;

/// <summary>Query of GET api/discord/guilds/{guildId}/members.</summary>
public sealed record DiscordMemberQuery(
    string? Status, string? Search, string? RankRoleId, string? Reconciliation, int Page, int PageSize);

/// <summary>
/// Reads of the DISCORD tab (spec § 11): guild summaries and detail, members, events and the
/// sync log. Guild summaries aggregate role combinations in SQL and never look a handle up:
/// suggestions are computed only by their own route.
///
/// Members are paged by the database whenever the page can be cut there: status, search and
/// order are SQL, and the rank, links and status of the page's rows only are computed after,
/// in a handful of queries whatever the guild size. A rank or reconciliation filter depends on
/// values computed in memory (the rank from the role configuration, the status from links
/// and the RSI roster), so it takes one bounded pass instead: the rows matching the SQL
/// filters (for a rank, only those holding the role, found by a LIKE on RoleIdsJson) are read
/// as (id, roles, bot) triples in order, ranked, reconciled when asked, and cut in memory;
/// only the page's rows are then read in full. The pass holds at most one small triple per
/// member of the guild (50,000 at most, the ingest bound), plus the links of those members
/// and the roster rows of their linked people, in a constant number of queries.
/// </summary>
public sealed class DiscordRosterQueryService(
    TrackerDbContext db,
    DiscordReconciliationService reconciliation,
    IOrganizationRepository organizations,
    CurrentUserAccessor currentUser)
{
    /// <summary>Default number of events and syncs returned.</summary>
    public const int DefaultLimit = 100;

    private static readonly IReadOnlyDictionary<string, DiscordRole> NoRoles = new Dictionary<string, DiscordRole>();

    /// <summary>Every tracked guild: unmapped ones first, then by name.</summary>
    public async Task<IReadOnlyList<DiscordGuildSummaryDto>> ListGuildsAsync(CancellationToken ct)
    {
        var guilds = await db.DiscordGuilds.AsNoTracking().ToListAsync(ct);
        if (guilds.Count == 0) return [];

        var combos = (await RoleCombosAsync(null, ct)).ToLookup(c => c.GuildId, StringComparer.Ordinal);
        var roles = (await db.DiscordRoles.AsNoTracking().ToListAsync(ct))
            .GroupBy(r => r.GuildId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToDictionary(r => r.RoleId, StringComparer.Ordinal), StringComparer.Ordinal);
        var lastSyncs = await LastSyncsAsync(null, ct);
        var orgNames = await organizations.GetLatestNamesBySidsAsync(
            guilds.Where(g => g.OrgSid is not null).Select(g => g.OrgSid!).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), ct);

        return guilds
            .Select(g =>
            {
                var summary = new DiscordGuildSummaryDto();
                FillSummary(
                    summary,
                    g,
                    roles.TryGetValue(g.GuildId, out var guildRoles) ? guildRoles : NoRoles,
                    combos[g.GuildId],
                    lastSyncs.GetValueOrDefault(g.GuildId),
                    g.OrgSid is null ? null : orgNames.GetValueOrDefault(g.OrgSid));
                return summary;
            })
            .OrderBy(s => s.OrgSid is null ? 0 : 1)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.GuildId, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>A guild with all its roles (deleted ones included), its org's RSI ranks and canEdit; null when unknown.</summary>
    public async Task<DiscordGuildDetailDto?> GetGuildAsync(string guildId, CancellationToken ct)
    {
        var guild = await db.DiscordGuilds.AsNoTracking().FirstOrDefaultAsync(g => g.GuildId == guildId, ct);
        if (guild is null) return null;

        var combos = await RoleCombosAsync(guildId, ct);
        var roles = await LoadRolesAsync(guildId, ct);
        var lastSync = (await LastSyncsAsync(guildId, ct)).GetValueOrDefault(guildId);
        string? orgName = null;
        IReadOnlyList<string> rsiRanks = [];
        if (guild.OrgSid is not null)
        {
            orgName = (await organizations.GetLatestNamesBySidsAsync([guild.OrgSid], ct)).GetValueOrDefault(guild.OrgSid);
            rsiRanks = await RsiRanksAsync(guild.OrgSid, ct);
        }

        var detail = new DiscordGuildDetailDto();
        FillSummary(detail, guild, roles, combos, lastSync, orgName);
        var holders = RoleMemberCounts(combos);
        detail.Roles = roles.Values
            .OrderBy(r => r.DeletedAt is null ? 0 : 1)
            .ThenByDescending(r => r.Position)
            .ThenBy(r => r.RoleId, StringComparer.Ordinal)
            .Select(r => new DiscordRoleDto
            {
                RoleId = r.RoleId,
                Name = r.Name,
                Position = r.Position,
                Color = r.Color,
                Hoist = r.Hoist,
                Managed = r.Managed,
                IsRank = r.IsRank,
                RankOrder = r.RankOrder,
                RsiRankLabel = r.RsiRankLabel,
                Deleted = r.DeletedAt is not null,
                MemberCount = holders.GetValueOrDefault(r.RoleId),
            })
            .ToList();
        detail.RsiRanks = rsiRanks;
        // The responsible-user rule of spec § 11 (the same rule guards the configuration routes).
        detail.CanEdit = guild.OrgSid is null
            || currentUser.IsAdmin
            || (guild.OrgMappedByApiUserId is long owner && owner == currentUser.UserId);
        return detail;
    }

    /// <summary>
    /// A page of members (see the class summary for how filters are paged). status: active
    /// (default), former or all, else 400; rankRoleId: a snowflake, else 400; reconciliation: a
    /// <see cref="DiscordReconciliationStatus"/> value, else 400, ignored for an unmapped guild.
    /// </summary>
    public async Task<PaginatedResponse<DiscordMemberDto>> GetMembersAsync(
        string guildId, DiscordMemberQuery query, CancellationToken ct)
    {
        bool? active = (query.Status ?? "active").Trim().ToLowerInvariant() switch
        {
            "active" => true,
            "former" => false,
            "all" => null,
            _ => throw new ValidationException("status doit valoir active, former ou all."),
        };
        var rankRoleId = string.IsNullOrWhiteSpace(query.RankRoleId) ? null : query.RankRoleId.Trim();
        if (rankRoleId is not null && !DiscordSnowflake.IsValid(rankRoleId))
            throw new ValidationException("rankRoleId doit être un identifiant de rôle Discord (17 à 20 chiffres).");
        var wanted = string.IsNullOrWhiteSpace(query.Reconciliation) ? null : query.Reconciliation.Trim().ToLowerInvariant();
        if (wanted is not null && !DiscordReconciliationStatus.IsValid(wanted))
            throw new ValidationException("reconciliation doit valoir rsi_unknown, unlinked, ok, rank_mismatch ou not_in_rsi_org.");
        var page = Paging.Page(query.Page);
        var pageSize = Paging.PageSize(query.PageSize);

        var guild = await db.DiscordGuilds.AsNoTracking()
                .Where(g => g.GuildId == guildId)
                .Select(g => new { g.OrgSid })
                .FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException("Serveur Discord inconnu.");
        // Spec § 10.2: members of an unmapped guild have no status, so the filter is ignored.
        if (guild.OrgSid is null) wanted = null;
        var roles = await LoadRolesAsync(guildId, ct);

        var rows = from m in db.DiscordMembers.AsNoTracking()
                   join a in db.DiscordAccounts.AsNoTracking() on m.DiscordUserId equals a.DiscordUserId
                   where m.GuildId == guildId
                   select new { Member = m, Account = a };
        if (active == true) rows = rows.Where(r => r.Member.LeftAt == null);
        if (active == false) rows = rows.Where(r => r.Member.LeftAt != null);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            // LIKE is case-insensitive for ASCII; the user's wildcards are escaped.
            var escaped = query.Search.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
            var pattern = $"%{escaped}%";
            rows = rows.Where(r =>
                EF.Functions.Like(r.Account.Username, pattern, "\\")
                || (r.Account.GlobalName != null && EF.Functions.Like(r.Account.GlobalName, pattern, "\\"))
                || (r.Member.Nick != null && EF.Functions.Like(r.Member.Nick, pattern, "\\")));
        }
        if (rankRoleId is not null)
        {
            // Necessary condition, decided by SQL: the member holds the role (a snowflake, so no
            // wildcard). Whether it is their rank depends on their other roles: decided below.
            var holds = $"%\"{rankRoleId}\"%";
            rows = rows.Where(r => EF.Functions.Like(r.Member.RoleIdsJson, holds));
        }
        var ordered = rows
            .OrderBy(r => EF.Functions.Collate(r.Member.Nick ?? r.Account.GlobalName ?? r.Account.Username, "NOCASE"))
            .ThenBy(r => r.Member.DiscordUserId);

        if (rankRoleId is null && wanted is null)
        {
            var total = await rows.CountAsync(ct);
            var pageRows = await ordered.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
            var ranks = pageRows.ToDictionary(r => r.Member.DiscordUserId, r => Rank(r.Member.RoleIdsJson, roles), StringComparer.Ordinal);
            var statuses = await reconciliation.ReconcileAsync(
                guild.OrgSid,
                pageRows.Select(r => new ReconciliationSubject(
                    r.Member.DiscordUserId, r.Account.IsBot,
                    DiscordReconciliationService.RsiRankLabelOf(ranks[r.Member.DiscordUserId], roles))).ToList(),
                ct);
            return PaginatedResponse<DiscordMemberDto>.Create(
                pageRows.Select(r => ToMemberDto(r.Member, r.Account, roles, ranks[r.Member.DiscordUserId], statuses[r.Member.DiscordUserId]))
                    .ToList(),
                page, pageSize, total);
        }

        // Bounded in-memory pass (class summary).
        var candidates = await ordered
            .Select(r => new { r.Member.DiscordUserId, r.Member.RoleIdsJson, r.Account.IsBot })
            .ToListAsync(ct);
        var ranked = candidates
            .Select(c => (c.DiscordUserId, c.IsBot, Rank: Rank(c.RoleIdsJson, roles)))
            .Where(c => rankRoleId is null || c.Rank?.RoleId == rankRoleId)
            .ToList();
        IReadOnlyDictionary<string, MemberReconciliation>? statusesOfAll = null;
        if (wanted is not null)
        {
            var all = await reconciliation.ReconcileAsync(
                guild.OrgSid,
                ranked.Select(c => new ReconciliationSubject(
                    c.DiscordUserId, c.IsBot, DiscordReconciliationService.RsiRankLabelOf(c.Rank, roles))).ToList(),
                ct);
            statusesOfAll = all;
            ranked = ranked.Where(c => all[c.DiscordUserId].Status == wanted).ToList();
        }

        var pageOfMembers = ranked.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        var ids = pageOfMembers.Select(c => c.DiscordUserId).ToList();
        var loaded = (await rows.Where(r => ids.Contains(r.Member.DiscordUserId)).ToListAsync(ct))
            .ToDictionary(r => r.Member.DiscordUserId, StringComparer.Ordinal);
        var pageStatuses = statusesOfAll ?? await reconciliation.ReconcileAsync(
            guild.OrgSid,
            pageOfMembers.Select(c => new ReconciliationSubject(
                c.DiscordUserId, c.IsBot, DiscordReconciliationService.RsiRankLabelOf(c.Rank, roles))).ToList(),
            ct);
        var items = pageOfMembers
            .Where(c => loaded.ContainsKey(c.DiscordUserId))
            .Select(c =>
            {
                var row = loaded[c.DiscordUserId];
                return ToMemberDto(row.Member, row.Account, roles, c.Rank, pageStatuses[c.DiscordUserId]);
            })
            .ToList();
        return PaginatedResponse<DiscordMemberDto>.Create(items, page, pageSize, ranked.Count);
    }

    /// <summary>
    /// The guild's history, newest Id first: its own events and the account-level events
    /// (renames, GuildId null) of accounts that are or were its members. type and userId filter
    /// both; limit is bounded by <see cref="Paging.Limit"/>.
    /// </summary>
    public async Task<IReadOnlyList<DiscordEventDto>> GetEventsAsync(
        string guildId, string? type, string? userId, int limit, CancellationToken ct)
    {
        limit = Paging.Limit(limit);
        await EnsureGuildAsync(guildId, ct);

        var guildEvents = db.DiscordMemberEvents.AsNoTracking().Where(e => e.GuildId == guildId);
        var memberIds = db.DiscordMembers.Where(m => m.GuildId == guildId).Select(m => m.DiscordUserId);
        var accountEvents = db.DiscordMemberEvents.AsNoTracking()
            .Where(e => e.GuildId == null && memberIds.Contains(e.DiscordUserId));
        if (!string.IsNullOrWhiteSpace(type))
        {
            var wantedType = type.Trim();
            guildEvents = guildEvents.Where(e => e.Type == wantedType);
            accountEvents = accountEvents.Where(e => e.Type == wantedType);
        }
        if (!string.IsNullOrWhiteSpace(userId))
        {
            var wantedUser = userId.Trim();
            guildEvents = guildEvents.Where(e => e.DiscordUserId == wantedUser);
            accountEvents = accountEvents.Where(e => e.DiscordUserId == wantedUser);
        }

        // Two index-served queries rather than one OR, then merged in Id order.
        var events = (await guildEvents.OrderByDescending(e => e.Id).Take(limit).ToListAsync(ct))
            .Concat(await accountEvents.OrderByDescending(e => e.Id).Take(limit).ToListAsync(ct))
            .OrderByDescending(e => e.Id)
            .Take(limit)
            .ToList();
        if (events.Count == 0) return [];

        var userIds = events.Select(e => e.DiscordUserId).Distinct(StringComparer.Ordinal).ToList();
        var usernames = await db.DiscordAccounts.AsNoTracking()
            .Where(a => userIds.Contains(a.DiscordUserId))
            .ToDictionaryAsync(a => a.DiscordUserId, a => a.Username, StringComparer.Ordinal, ct);
        var syncIds = events.Select(e => e.SyncId).Distinct().ToList();
        var senders = await db.DiscordSyncs.AsNoTracking()
            .Where(s => syncIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => s.SubmittedByUsername, ct);
        var roles = await LoadRolesAsync(guildId, ct);

        return events.Select(e => new DiscordEventDto
        {
            Id = e.Id,
            GuildId = e.GuildId,
            DiscordUserId = e.DiscordUserId,
            Username = usernames.GetValueOrDefault(e.DiscordUserId),
            Type = e.Type,
            OldValue = e.OldValue,
            NewValue = e.NewValue,
            OccurredAt = e.OccurredAt,
            NotBefore = e.NotBefore,
            ObservedAt = e.ObservedAt,
            SubmittedBy = senders.GetValueOrDefault(e.SyncId),
            RankChange = e.Type == DiscordEventTypes.RolesChanged ? RankChange(e, roles) : null,
        }).ToList();
    }

    /// <summary>The guild's sync log, newest first, bounded by <see cref="Paging.Limit"/>.</summary>
    public async Task<IReadOnlyList<DiscordSyncDto>> GetSyncsAsync(string guildId, int limit, CancellationToken ct)
    {
        limit = Paging.Limit(limit);
        await EnsureGuildAsync(guildId, ct);
        return await db.DiscordSyncs.AsNoTracking()
            .Where(s => s.GuildId == guildId)
            .OrderByDescending(s => s.Id)
            .Take(limit)
            .Select(s => new DiscordSyncDto
            {
                Id = s.Id,
                ReceivedAt = s.ReceivedAt,
                CollectedAt = s.CollectedAt,
                SubmittedBy = s.SubmittedByUsername,
                Method = s.Method,
                DeclaredComplete = s.DeclaredComplete,
                IsComplete = s.IsComplete,
                IsBaseline = s.IsBaseline,
                DepartureGuardTripped = s.DepartureGuardTripped,
                ExpectedCount = s.ExpectedCount,
                CollectedCount = s.CollectedCount,
                OptedOutCount = s.OptedOutCount,
                UnknownRoleRefCount = s.UnknownRoleRefCount,
                EventCount = s.EventCount,
                PluginVersion = s.PluginVersion,
            })
            .ToListAsync(ct);
    }

    private static void FillSummary(
        DiscordGuildSummaryDto dto, DiscordGuild guild, IReadOnlyDictionary<string, DiscordRole> roles,
        IEnumerable<RoleCombo> combos, DiscordSync? lastSync, string? orgName)
    {
        var active = 0;
        var perRank = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var combo in combos)
        {
            active += combo.Count;
            if (Rank(combo.RoleIdsJson, roles) is { } rank)
                perRank[rank.RoleId] = perRank.GetValueOrDefault(rank.RoleId) + combo.Count;
        }
        var ranks = perRank.Keys.Select(id => roles[id]).ToList();
        ranks.Sort(DiscordRankResolver.Compare);

        dto.GuildId = guild.GuildId;
        dto.Name = guild.Name;
        dto.IconHash = guild.IconHash;
        dto.OrgSid = guild.OrgSid;
        dto.OrgName = orgName;
        dto.OrgMappedBy = guild.OrgMappedByUsername;
        dto.ActiveMembers = active;
        dto.RankDistribution = ranks
            .Select(r => new DiscordRankCountDto { RoleId = r.RoleId, Name = r.Name, Color = r.Color, Count = perRank[r.RoleId] })
            .ToList();
        dto.LastSync = lastSync is null
            ? null
            : new DiscordLastSyncDto
            {
                ReceivedAt = lastSync.ReceivedAt,
                IsComplete = lastSync.IsComplete,
                Method = lastSync.Method,
                SubmittedBy = lastSync.SubmittedByUsername,
                DepartureGuardTripped = lastSync.DepartureGuardTripped,
            };
        dto.LastCompleteSyncAt = guild.LastCompleteSyncAt;
    }

    /// <summary>Active, non-bot members grouped by (guild, role list): a few rows per guild, however many members.</summary>
    private async Task<List<RoleCombo>> RoleCombosAsync(string? guildId, CancellationToken ct)
    {
        var humans = from m in db.DiscordMembers.AsNoTracking()
                     join a in db.DiscordAccounts.AsNoTracking() on m.DiscordUserId equals a.DiscordUserId
                     where m.LeftAt == null && !a.IsBot
                     select m;
        if (guildId is not null) humans = humans.Where(m => m.GuildId == guildId);
        return await humans
            .GroupBy(m => new { m.GuildId, m.RoleIdsJson })
            .Select(g => new RoleCombo(g.Key.GuildId, g.Key.RoleIdsJson, g.Count()))
            .ToListAsync(ct);
    }

    private async Task<Dictionary<string, DiscordSync>> LastSyncsAsync(string? guildId, CancellationToken ct)
    {
        var syncs = db.DiscordSyncs.AsNoTracking();
        if (guildId is not null) syncs = syncs.Where(s => s.GuildId == guildId);
        var lastIds = syncs.GroupBy(s => s.GuildId).Select(g => g.Max(s => s.Id));
        return await db.DiscordSyncs.AsNoTracking()
            .Where(s => lastIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.GuildId, StringComparer.Ordinal, ct);
    }

    /// <summary>Distinct trimmed ranks of the org's active roster (case-insensitive), highest stars first.</summary>
    private async Task<IReadOnlyList<string>> RsiRanksAsync(string orgSid, CancellationToken ct)
    {
        var rows = await db.OrganizationMembers.AsNoTracking()
            .Where(m => m.OrgSid == orgSid && m.IsActive && m.Rank != null)
            .Select(m => new { m.Rank, m.Stars })
            .Distinct()
            .ToListAsync(ct);
        return rows
            .Select(r => new { Rank = r.Rank!.Trim(), r.Stars })
            .Where(r => r.Rank.Length > 0)
            .GroupBy(r => r.Rank, StringComparer.OrdinalIgnoreCase)
            .Select(g => new
            {
                Rank = g.Select(r => r.Rank).OrderBy(r => r, StringComparer.Ordinal).First(),
                Stars = g.Max(r => r.Stars),
            })
            .OrderByDescending(r => r.Stars ?? -1)
            .ThenBy(r => r.Rank, StringComparer.OrdinalIgnoreCase)
            .Select(r => r.Rank)
            .ToList();
    }

    private async Task<Dictionary<string, DiscordRole>> LoadRolesAsync(string guildId, CancellationToken ct)
        => (await db.DiscordRoles.AsNoTracking().Where(r => r.GuildId == guildId).ToListAsync(ct))
            .ToDictionary(r => r.RoleId, StringComparer.Ordinal);

    private async Task EnsureGuildAsync(string guildId, CancellationToken ct)
    {
        if (!await db.DiscordGuilds.AnyAsync(g => g.GuildId == guildId, ct))
            throw new NotFoundException("Serveur Discord inconnu.");
    }

    private static RankRole? Rank(string roleIdsJson, IReadOnlyDictionary<string, DiscordRole> roles)
        => DiscordRankResolver.Resolve(DiscordRoleLists.ParseRoleIds(roleIdsJson), roles);

    private static Dictionary<string, int> RoleMemberCounts(IEnumerable<RoleCombo> combos)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var combo in combos)
        {
            foreach (var roleId in DiscordRoleLists.ParseRoleIds(combo.RoleIdsJson).Distinct(StringComparer.Ordinal))
                counts[roleId] = counts.GetValueOrDefault(roleId) + combo.Count;
        }
        return counts;
    }

    /// <summary>
    /// The rank change of a roles_changed event, resolved with the current rank configuration
    /// (spec § 9.5) and named as the event stored the roles then. Null when the rank is the same.
    /// </summary>
    private static DiscordRankChangeDto? RankChange(DiscordMemberEvent e, IReadOnlyDictionary<string, DiscordRole> roles)
    {
        var before = DiscordRoleLists.ParseEventRoles(e.OldValue);
        var after = DiscordRoleLists.ParseEventRoles(e.NewValue);
        var from = DiscordRankResolver.Resolve(before.Select(r => r.Id), roles);
        var to = DiscordRankResolver.Resolve(after.Select(r => r.Id), roles);
        if (from?.RoleId == to?.RoleId) return null;
        return new DiscordRankChangeDto { From = NameThen(from, before), To = NameThen(to, after) };
    }

    private static string? NameThen(RankRole? rank, IReadOnlyList<EventRole> stored)
    {
        if (rank is null) return null;
        var then = stored.FirstOrDefault(r => r.Id == rank.RoleId)?.Name;
        return string.IsNullOrEmpty(then) ? rank.Name : then;
    }

    private static DiscordMemberDto ToMemberDto(
        DiscordMember member, DiscordAccount account, IReadOnlyDictionary<string, DiscordRole> roles,
        RankRole? rank, MemberReconciliation reconciled) => new()
    {
        DiscordUserId = member.DiscordUserId,
        Username = account.Username,
        GlobalName = account.GlobalName,
        Nick = member.Nick,
        IsBot = account.IsBot,
        JoinedAt = member.JoinedAt,
        FirstSeenAt = member.FirstSeenAt,
        LastSeenAt = member.LastSeenAt,
        LeftAt = member.LeftAt,
        Rank = rank is null ? null : new DiscordRankDto { RoleId = rank.RoleId, Name = rank.Name, Color = rank.Color },
        Roles = DiscordRoleLists.ParseRoleIds(member.RoleIdsJson)
            .Distinct(StringComparer.Ordinal)
            .Select(id => roles.TryGetValue(id, out var role) ? role : null)
            .OfType<DiscordRole>()
            .Where(r => r.DeletedAt is null)
            .OrderByDescending(r => r.Position)
            .ThenBy(r => r.RoleId, StringComparer.Ordinal)
            .Select(r => new DiscordRankDto { RoleId = r.RoleId, Name = r.Name, Color = r.Color })
            .ToList(),
        Links = reconciled.Links,
        RsiRank = reconciled.RsiRank,
        Reconciliation = reconciled.Status,
        MultipleLinks = reconciled.MultipleLinks,
    };

    private sealed record RoleCombo(string GuildId, string RoleIdsJson, int Count);
}
```

- [ ] **Step 5: Add the routes and register the service**

In `src/Collector.Api/Controllers/DiscordRostersController.cs`, replace:

```csharp
using Collector.Api.Dtos.Discord;
using Collector.Api.Services.Discord;
```

with:

```csharp
using Collector.Api.Dtos.Discord;
using Collector.Api.Errors;
using Collector.Api.Services.Discord;
```

and replace the end of the class:

```csharp
        => Ok(await reconciliation.GetMultiMembershipAsync(page, pageSize, ct));
}
```

with:

```csharp
        => Ok(await reconciliation.GetMultiMembershipAsync(page, pageSize, ct));

    /// <summary>Tracked guilds, unmapped ones first, then by name. Never computes suggestions.</summary>
    [HttpGet("discord/guilds")]
    public async Task<ActionResult<IReadOnlyList<DiscordGuildSummaryDto>>> ListGuilds(
        [FromServices] DiscordRosterQueryService queries, CancellationToken ct)
        => Ok(await queries.ListGuildsAsync(ct));

    /// <summary>A guild with its roles, its org's RSI ranks and whether the caller may configure it.</summary>
    [HttpGet("discord/guilds/{guildId}")]
    public async Task<ActionResult<DiscordGuildDetailDto>> GetGuild(
        string guildId, [FromServices] DiscordRosterQueryService queries, CancellationToken ct)
        => Ok(await queries.GetGuildAsync(guildId, ct) ?? throw new NotFoundException("Serveur Discord inconnu."));

    /// <summary>A page of the guild's members with rank, links and reconciliation status.</summary>
    [HttpGet("discord/guilds/{guildId}/members")]
    public async Task<ActionResult<PaginatedResponse<DiscordMemberDto>>> GetGuildMembers(
        string guildId,
        [FromServices] DiscordRosterQueryService queries,
        [FromQuery] string? status = "active",
        [FromQuery] string? search = null,
        [FromQuery] string? rankRoleId = null,
        [FromQuery] string? reconciliation = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
        => Ok(await queries.GetMembersAsync(
            guildId, new DiscordMemberQuery(status, search, rankRoleId, reconciliation, page, pageSize), ct));

    /// <summary>The guild's history, newest first, with the sender of each source sync.</summary>
    [HttpGet("discord/guilds/{guildId}/events")]
    public async Task<ActionResult<IReadOnlyList<DiscordEventDto>>> GetGuildEvents(
        string guildId,
        [FromServices] DiscordRosterQueryService queries,
        [FromQuery] string? type = null,
        [FromQuery] string? userId = null,
        [FromQuery] int limit = DiscordRosterQueryService.DefaultLimit,
        CancellationToken ct = default)
        => Ok(await queries.GetEventsAsync(guildId, type, userId, limit, ct));

    /// <summary>The guild's sync log, newest first.</summary>
    [HttpGet("discord/guilds/{guildId}/syncs")]
    public async Task<ActionResult<IReadOnlyList<DiscordSyncDto>>> GetGuildSyncs(
        string guildId,
        [FromServices] DiscordRosterQueryService queries,
        [FromQuery] int limit = DiscordRosterQueryService.DefaultLimit,
        CancellationToken ct = default)
        => Ok(await queries.GetSyncsAsync(guildId, limit, ct));
}
```

In `src/Collector.Api/Extensions/ServiceCollectionExtensions.cs`, method `AddApiServices`, replace:

```csharp
        services.AddHostedService<AudioOrphanSweeper>();
```

with:

```csharp
        services.AddHostedService<AudioOrphanSweeper>();

        // Discord roster reads: guilds, members, events and syncs (spec § 11).
        services.AddScoped<Collector.Api.Services.Discord.DiscordRosterQueryService>();
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test src/Collector.Api.Tests --filter "FullyQualifiedName~DiscordRosterQueryTests"`

Expected: `Passed!  - Failed:     0, Passed:     6, Skipped:     0, Total:     6`

Run: `dotnet test src/Collector.Api.Tests --filter "FullyQualifiedName~DiscordRosterQueryCostTests"`

Expected: `Passed!  - Failed:     0, Passed:     5, Skipped:     0, Total:     5`

- [ ] **Step 7: Check that nothing regressed and the new routes stay closed**

Run: `dotnet test src/Collector.Api.Tests --filter "FullyQualifiedName~DiscordReconciliationTests|FullyQualifiedName~AuthorizationTests"`

Expected: `Passed!` with `Failed:     0` (the five new routes answer 401 to an anonymous caller and to a `discord:ingest` key).

- [ ] **Step 8: Commit**

```bash
git add src/Collector.Api/Services/Discord/DiscordRosterQueryService.cs \
        src/Collector.Api/Dtos/Discord/DiscordRosterDtos.cs \
        src/Collector.Api/Controllers/DiscordRostersController.cs \
        src/Collector.Api/Extensions/ServiceCollectionExtensions.cs \
        src/Collector.Api.Tests/Discord/DiscordRosterQueryTests.cs \
        src/Collector.Api.Tests/Discord/DiscordRosterQueryCostTests.cs
git commit -F - <<'EOF'
feat(api): read discord guilds, members, events and syncs

The DISCORD tab needs the tracked guilds, their members with rank, links
and reconciliation status, their history and their sync log. Guild
summaries aggregate role combinations in SQL and never look a handle up,
so the list stays cheap next to a 20,000-member guild. Members are paged
by the database; a rank or status filter depends on values computed in
memory, so it takes one bounded pass over small triples instead of a query
per member, and only the page's rows are read in full. A role change that
moves a member's rank is labelled from the current rank configuration,
with the role names stored at the time, and each event names the sender
of its source sync.

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
EOF
```

### Task C4: Cross profile and org guilds

**Files:**
- Create: `src/Collector.Api/Services/Discord/DiscordProfileService.cs`
- Modify: `src/Collector.Api/Dtos/Discord/DiscordRosterDtos.cs` (after the last class, `DiscordSyncDto`)
- Modify: `src/Collector.Api/Controllers/DiscordRostersController.cs` (after the `GetGuildSyncs` action)
- Modify: `src/Collector.Api/Extensions/ServiceCollectionExtensions.cs` (`AddApiServices`, after `services.AddHostedService<AudioOrphanSweeper>();`)
- Test: `src/Collector.Api.Tests/Discord/DiscordProfileTests.cs`

**Interfaces:**
- Consumes:
  - CONTRACTS § 1 (lot A): `DiscordGuild`, `DiscordRole`, `DiscordAccount`, `DiscordMember`, `DiscordMemberEvent`, `DiscordEventTypes` and their DbSets; indexes `IX_users_UserHandle_NoCase`, `IX_user_handle_history_UserHandle_NoCase` (migration `AddDiscordRosters`) and the existing `IX_organization_members_UserHandle_NoCase`.
  - CONTRACTS § 5 (lot A): `DiscordTestKit.NewSnowflake()`.
  - Task C1: `DiscordRankResolver.Resolve`, `DiscordRoleLists.ParseRoleIds`.
  - Task C2: `DiscordRostersController` (non-partial, `[FromServices]` actions), `DiscordRosterDtos.cs`, test helper `DiscordReadSeed` (`SeedGuildAsync`, `SeedRoleAsync`, `SeedMemberAsync`, `SeedPersonAsync`, `SeedRosterAsync`, `SetGuildOrgAsync`, `WithDbAsync`, `At`).
  - Task C3: the `GetGuildSyncs` action (edit anchor) and `DiscordSyncDto` (edit anchor).
  - Existing: `TrackerDbContext` (`Users`, `UserHandleHistories`, `TrackedEntities`, `EntityLinks`, `OrganizationMembers`, `ChangeEvents`), `LinkProviders.Discord`, `NotFoundException`.
- Produces:
  - `Collector.Api.Services.Discord.DiscordProfileService` (scoped): `Task<DiscordUserProfileDto> GetUserProfileAsync(string handle, CancellationToken ct)` (404 `NotFoundException` when the handle is unknown everywhere), `Task<IReadOnlyList<DiscordOrgGuildDto>> GetOrgGuildsAsync(string sid, CancellationToken ct)`, `const int MaxTimelineEntries = 100`.
  - DTOs appended to `DiscordRosterDtos.cs`: `DiscordProfileGuildDto`, `DiscordProfileAccountDto`, `DiscordTimelineSources` (`Rsi = "rsi"`, `Discord = "discord"`), `DiscordTimelineEntryDto`, `DiscordUserProfileDto`, `DiscordOrgGuildDto` (CONTRACTS § 7 JSON).
  - Routes (CONTRACTS § 7): `GET api/users/{handle}/discord`, `GET api/organizations/{sid}/discord`.
  - Decisions:
    - The person is resolved as LinksController/MembershipsController do, regardless of case: `users` (latest `UpdatedAt`), else `user_handle_history` (latest `LastSeen`) give the citizen id; the entity is the one of that citizen id, else the entity whose `CurrentHandle` matches (never one bound to another citizen id). A handle known only to `organization_members` is known too: 200 with empty lists. 404 only when no source knows it.
    - "No Discord data" (no entity, no discord link, or links to ids no sync ever carried) gives `{ accounts: [], timeline: [] }`: the combined timeline is only built for a person with at least one known Discord account (the RSI history alone is already on the citizen page).
    - Timeline: RSI entries keep an old handle's `change_events` strictly before the `FirstSeen` of the citizen's next handle; the current handle is unbounded; there is no lower bound (spec § 10.4). Discord entries are dated `OccurredAt ?? ObservedAt`, with `notBefore` only when the exact date is unknown. Ties: Discord first, then newest id.
    - `lastSyncComplete` = `LastCompleteSyncAt == LastCollectedAt` (the last accepted sync was complete and not guarded).

- [ ] **Step 1: Write the failing tests**

Create `src/Collector.Api.Tests/Discord/DiscordProfileTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Collector.Models;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Collector.Api.Tests.Discord;

/// <summary>
/// GET api/users/{handle}/discord (spec § 10.4) and GET api/organizations/{sid}/discord:
/// linked accounts and their guilds, the combined RSI + Discord timeline within each
/// handle's window, empty lists versus 404, and org guilds that follow re-mapping.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class DiscordProfileTests(ApiFactory factory)
{
    private static int _seq = 74_000;
    private readonly DiscordReadSeed _seed = new(factory);

    private static DateTime At => DiscordReadSeed.At;

    private static int Next() => Interlocked.Increment(ref _seq);

    /// <summary>A citizen id no other test uses.</summary>
    private static int Cid(int n, int k) => 1_740_000_000 + n * 20 + k;

    [Fact]
    public async Task AProfile_ListsLinkedAccountsAndGuilds_WithACombinedTimeline_WithinEachHandlesWindow()
    {
        var n = Next();
        var cid = Cid(n, 1);
        var sid = $"PRF{n}";
        var renamedAt = At.AddDays(-50);
        await SeedUserAsync(cid, $"Pilot{n}");
        await SeedHistoryAsync(cid, $"OldPilot{n}", firstSeen: At.AddDays(-100), lastSeen: renamedAt.AddDays(-1));
        await SeedHistoryAsync(cid, $"Pilot{n}", firstSeen: renamedAt, lastSeen: At);
        var discordId = DiscordTestKit.NewSnowflake();
        var entityId = await _seed.SeedPersonAsync(cid, $"Pilot{n}", "Pilot", discordId);
        // A legacy link that is not a Discord id: no account, ignored.
        await SeedLinkAsync(entityId, "pilot#1234");
        var mapped = await _seed.SeedGuildAsync(sid, name: $"Alpha {n}");
        var unmapped = await _seed.SeedGuildAsync(null, name: $"Bravo {n}");
        var officier = await _seed.SeedRoleAsync(mapped, "Officier", 20, isRank: true);
        await _seed.SeedMemberAsync(mapped, $"pilot{n}", [officier], globalName: "Pilot", userId: discordId, joinedAt: At.AddDays(-40));
        await _seed.SeedMemberAsync(unmapped, $"pilot{n}", userId: discordId, left: true, joinedAt: At.AddDays(-45));
        await SeedChangeAsync($"OldPilot{n}", "member_joined", sid, At.AddDays(-80));
        // After the rename the old handle may belong to someone else: out of this timeline.
        await SeedChangeAsync($"OldPilot{n}", "rank_changed", $"OTHER{n}", At.AddDays(-10));
        await SeedChangeAsync($"Pilot{n}", "handle_changed", sid, renamedAt);
        await SeedChangeAsync($"Pilot{n}", "member_left", sid, At.AddDays(-5));
        // Not a timeline type.
        await SeedChangeAsync($"Pilot{n}", "display_name_changed", null, At.AddDays(-4));
        await SeedDiscordEventAsync(mapped, discordId, DiscordEventTypes.Joined, observedAt: At.AddDays(-39), occurredAt: At.AddDays(-40));
        await SeedDiscordEventAsync(unmapped, discordId, DiscordEventTypes.Left, observedAt: At.AddDays(-2), notBefore: At.AddDays(-3));
        await SeedDiscordEventAsync(null, discordId, DiscordEventTypes.UsernameChanged, observedAt: At.AddDays(-1),
            notBefore: At.AddDays(-2), oldValue: $"old{n}", newValue: $"pilot{n}");
        var client = await factory.SignedInClientAsync($"c4-profile-{n}");

        var profile = await GetOkAsync(client, $"/api/users/PILOT{n}/discord");

        var account = profile.GetProperty("accounts").EnumerateArray().Should().ContainSingle().Subject;
        account.GetProperty("discordUserId").GetString().Should().Be(discordId);
        account.GetProperty("username").GetString().Should().Be($"pilot{n}");
        account.GetProperty("globalName").GetString().Should().Be("Pilot");
        account.GetProperty("guilds").EnumerateArray()
            .Select(g => (g.GetProperty("guildId").GetString(), g.GetProperty("guildName").GetString(),
                g.GetProperty("orgSid").GetString(), g.GetProperty("rank").GetString(),
                Date(g.GetProperty("joinedAt")), Date(g.GetProperty("leftAt")), g.GetProperty("lastSeenAt").GetDateTime()))
            .Should().Equal(
                (mapped, $"Alpha {n}", sid, "Officier", At.AddDays(-40), null, At),
                (unmapped, $"Bravo {n}", null, null, At.AddDays(-45), At, At));
        profile.GetProperty("timeline").EnumerateArray().Select(Entry).Should().Equal(
            ("discord", "username_changed", At.AddDays(-1), At.AddDays(-2), null, null, null),
            ("discord", "left", At.AddDays(-2), At.AddDays(-3), null, unmapped, $"Bravo {n}"),
            ("rsi", "member_left", At.AddDays(-5), null, sid, null, null),
            ("discord", "joined", At.AddDays(-40), null, sid, mapped, $"Alpha {n}"),
            ("rsi", "handle_changed", renamedAt, null, sid, null, null),
            ("rsi", "member_joined", At.AddDays(-80), null, sid, null, null));
    }

    [Fact]
    public async Task AFormerHandle_ACaseVariant_OrAnEntityWithoutCitizenId_ResolveThePerson()
    {
        var n = Next();
        var cid = Cid(n, 1);
        await SeedUserAsync(cid, $"Current{n}");
        await SeedHistoryAsync(cid, $"Former{n}", firstSeen: At.AddDays(-60), lastSeen: At.AddDays(-31));
        await SeedHistoryAsync(cid, $"Current{n}", firstSeen: At.AddDays(-30), lastSeen: At);
        var discordId = DiscordTestKit.NewSnowflake();
        await _seed.SeedPersonAsync(cid, $"Current{n}", null, discordId);
        var guildId = await _seed.SeedGuildAsync(null);
        await _seed.SeedMemberAsync(guildId, $"current{n}", userId: discordId);
        // A person with no citizen id, linked by hand: their RSI part reads their current handle.
        var rosterOnlyId = DiscordTestKit.NewSnowflake();
        await _seed.SeedPersonAsync(null, $"RosterOnly{n}", null, rosterOnlyId);
        await _seed.SeedMemberAsync(guildId, $"rosteronly{n}", userId: rosterOnlyId);
        await SeedChangeAsync($"RosterOnly{n}", "member_joined", $"RO{n}", At.AddDays(-7));
        var client = await factory.SignedInClientAsync($"c4-resolve-{n}");

        foreach (var handle in new[] { $"former{n}", $"CURRENT{n}", $"Current{n}" })
        {
            var profile = await GetOkAsync(client, $"/api/users/{handle}/discord");
            AccountIds(profile).Should().Equal(new[] { discordId }, handle);
        }
        var rosterOnly = await GetOkAsync(client, $"/api/users/rosteronly{n}/discord");
        AccountIds(rosterOnly).Should().Equal(rosterOnlyId);
        rosterOnly.GetProperty("timeline").EnumerateArray().Select(Entry).Should().Equal(
            ("rsi", "member_joined", At.AddDays(-7), null, $"RO{n}", null, null));
    }

    [Fact]
    public async Task APersonWithoutDiscordData_GetsEmptyLists_AndAnUnknownHandleIs404()
    {
        var n = Next();
        // Known to users only.
        await SeedUserAsync(Cid(n, 1), $"UserOnly{n}");
        // Tracked, with RSI history, but no discord link.
        await SeedUserAsync(Cid(n, 2), $"NoDiscord{n}");
        await _seed.SeedPersonAsync(Cid(n, 2), $"NoDiscord{n}", null);
        await SeedChangeAsync($"NoDiscord{n}", "member_joined", $"ND{n}", At.AddDays(-3));
        // Linked to a Discord id no sync ever carried.
        await _seed.SeedPersonAsync(Cid(n, 3), $"NeverSynced{n}", null, DiscordTestKit.NewSnowflake());
        // Known to an org roster only.
        await _seed.SeedRosterAsync($"RO{n}", $"RosterOnly{n}", null, "Member");
        var client = await factory.SignedInClientAsync($"c4-empty-{n}");

        foreach (var handle in new[] { $"UserOnly{n}", $"nodiscord{n}", $"NeverSynced{n}", $"rosteronly{n}" })
        {
            var profile = await GetOkAsync(client, $"/api/users/{handle}/discord");
            profile.GetProperty("accounts").GetArrayLength().Should().Be(0, handle);
            profile.GetProperty("timeline").GetArrayLength().Should().Be(0, handle);
        }
        var unknown = await client.GetAsync($"/api/users/Nobody{n}/discord");
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var problem = await unknown.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("title").GetString().Should().Be("Not Found");
        problem.GetProperty("detail").GetString().Should().Contain($"Nobody{n}");
    }

    [Fact]
    public async Task AnOrg_ListsTheGuildsMappedToIt_WithTheirHeadcounts()
    {
        var n = Next();
        var sid = $"POG{n}";
        var zeta = await _seed.SeedGuildAsync(sid, name: $"Zeta {n}");
        var alpha = await _seed.SeedGuildAsync(sid, name: $"alpha {n}", complete: false);
        var middle = await _seed.SeedGuildAsync(sid, name: $"Mid {n}");
        // Its last complete sync is older than its last sync: that one was partial.
        await _seed.WithDbAsync(db => db.DiscordGuilds.Where(g => g.GuildId == middle)
            .ExecuteUpdateAsync(s => s.SetProperty(g => g.LastCompleteSyncAt, (DateTime?)At.AddDays(-7))));
        await _seed.SeedGuildAsync(null, name: $"Unmapped {n}");
        var first = await _seed.SeedMemberAsync(zeta, $"og-a{n}");
        var second = await _seed.SeedMemberAsync(zeta, $"og-b{n}");
        await _seed.SeedMemberAsync(zeta, $"og-c{n}");
        var bot = await _seed.SeedMemberAsync(zeta, $"og-bot{n}", bot: true);
        var gone = await _seed.SeedMemberAsync(zeta, $"og-gone{n}", left: true);
        await _seed.SeedPersonAsync(Cid(n, 1), $"OgA{n}", null, first);
        await _seed.SeedPersonAsync(Cid(n, 2), $"OgB{n}", null, second);
        await _seed.SeedPersonAsync(Cid(n, 3), $"OgBot{n}", null, bot);
        await _seed.SeedPersonAsync(Cid(n, 4), $"OgGone{n}", null, gone);
        var client = await factory.SignedInClientAsync($"c4-org-{n}");

        var guilds = (await GetOkAsync(client, $"/api/organizations/{sid.ToLowerInvariant()}/discord")).EnumerateArray().ToList();

        guilds.Select(g => (g.GetProperty("guildId").GetString(), g.GetProperty("name").GetString(),
                g.GetProperty("activeMembers").GetInt32(), g.GetProperty("linkedMembers").GetInt32(),
                g.GetProperty("lastSyncComplete").GetBoolean()))
            .Should().Equal(
                (alpha, $"alpha {n}", 0, 0, false),
                (middle, $"Mid {n}", 0, 0, false),
                (zeta, $"Zeta {n}", 3, 2, true));
        guilds[2].GetProperty("lastSyncAt").GetDateTime().Should().Be(At);
        guilds[2].GetProperty("iconHash").ValueKind.Should().Be(JsonValueKind.Null);
        (await GetOkAsync(client, $"/api/organizations/NONE{n}/discord")).GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task ARemappedGuild_MovesFromTheOldOrgsListToTheNewOne()
    {
        var n = Next();
        var sidA = $"PRA{n}";
        var sidB = $"PRB{n}";
        var guildId = await _seed.SeedGuildAsync(sidA);
        var client = await factory.SignedInClientAsync($"c4-remap-{n}");

        (await OrgGuildIdsAsync(client, sidA)).Should().Equal(guildId);
        (await OrgGuildIdsAsync(client, sidB)).Should().BeEmpty();

        await _seed.SetGuildOrgAsync(guildId, sidB);

        (await OrgGuildIdsAsync(client, sidA)).Should().BeEmpty();
        (await OrgGuildIdsAsync(client, sidB)).Should().Equal(guildId);
    }

    private static async Task<JsonElement> GetOkAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        response.StatusCode.Should().Be(HttpStatusCode.OK, url);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<List<string?>> OrgGuildIdsAsync(HttpClient client, string sid)
        => (await GetOkAsync(client, $"/api/organizations/{sid}/discord")).EnumerateArray()
            .Select(g => g.GetProperty("guildId").GetString()).ToList();

    private static List<string?> AccountIds(JsonElement profile)
        => profile.GetProperty("accounts").EnumerateArray().Select(a => a.GetProperty("discordUserId").GetString()).ToList();

    private static (string?, string?, DateTime, DateTime?, string?, string?, string?) Entry(JsonElement e) => (
        e.GetProperty("source").GetString(),
        e.GetProperty("type").GetString(),
        e.GetProperty("at").GetDateTime(),
        Date(e.GetProperty("notBefore")),
        e.GetProperty("orgSid").GetString(),
        e.GetProperty("guildId").GetString(),
        e.GetProperty("guildName").GetString());

    private static DateTime? Date(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null : value.GetDateTime();

    private Task SeedUserAsync(int citizenId, string handle) => _seed.WithDbAsync(async db =>
    {
        db.Users.Add(new User { CitizenId = citizenId, UserHandle = handle, CreatedAt = At, UpdatedAt = At });
        await db.SaveChangesAsync();
    });

    private Task SeedHistoryAsync(int citizenId, string handle, DateTime firstSeen, DateTime lastSeen) => _seed.WithDbAsync(async db =>
    {
        db.UserHandleHistories.Add(new UserHandleHistory
        {
            CitizenId = citizenId, UserHandle = handle, FirstSeen = firstSeen, LastSeen = lastSeen,
        });
        await db.SaveChangesAsync();
    });

    private Task SeedLinkAsync(long entityId, string value) => _seed.WithDbAsync(async db =>
    {
        db.EntityLinks.Add(new EntityLink
        {
            TrackedEntityId = entityId, Provider = LinkProviders.Discord, Value = value,
            AuthorApiUserId = 0, AuthorUsername = "seed", CreatedAt = At, UpdatedAt = At,
        });
        await db.SaveChangesAsync();
    });

    private Task SeedChangeAsync(string handle, string type, string? orgSid, DateTime at) => _seed.WithDbAsync(async db =>
    {
        db.ChangeEvents.Add(new ChangeEvent
        {
            Timestamp = at, EntityType = "member", EntityId = handle, ChangeType = type,
            OrgSid = orgSid, UserHandle = handle,
        });
        await db.SaveChangesAsync();
    });

    private Task SeedDiscordEventAsync(
        string? guildId, string userId, string type, DateTime observedAt, DateTime? occurredAt = null,
        DateTime? notBefore = null, string? oldValue = null, string? newValue = null) => _seed.WithDbAsync(async db =>
    {
        db.DiscordMemberEvents.Add(new DiscordMemberEvent
        {
            GuildId = guildId, DiscordUserId = userId, SyncId = 1, Type = type, OldValue = oldValue, NewValue = newValue,
            OccurredAt = occurredAt, NotBefore = notBefore, ObservedAt = observedAt,
        });
        await db.SaveChangesAsync();
    });
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test src/Collector.Api.Tests --filter "FullyQualifiedName~DiscordProfileTests"`

Expected: 5 failures, each `Expected response.StatusCode to be HttpStatusCode.OK {value: 200} because /api/…/discord, but found HttpStatusCode.NotFound {value: 404}` (the two routes do not exist yet).

- [ ] **Step 3: Add the DTOs**

In `src/Collector.Api/Dtos/Discord/DiscordRosterDtos.cs`, replace the end of `DiscordSyncDto`:

```csharp
    public int EventCount { get; set; }
    public string PluginVersion { get; set; } = null!;
}
```

with:

```csharp
    public int EventCount { get; set; }
    public string PluginVersion { get; set; } = null!;
}

/// <summary>A guild a linked Discord account is or was a member of.</summary>
public sealed class DiscordProfileGuildDto
{
    public string GuildId { get; set; } = null!;
    public string GuildName { get; set; } = null!;
    public string? OrgSid { get; set; }

    /// <summary>Current rank, or the last one for a departed member.</summary>
    public string? Rank { get; set; }

    public DateTime? JoinedAt { get; set; }
    public DateTime? LeftAt { get; set; }
    public DateTime LastSeenAt { get; set; }
}

/// <summary>A Discord account linked to the person, with its guilds (current first).</summary>
public sealed class DiscordProfileAccountDto
{
    public string DiscordUserId { get; set; } = null!;
    public string Username { get; set; } = null!;
    public string? GlobalName { get; set; }
    public IReadOnlyList<DiscordProfileGuildDto> Guilds { get; set; } = [];
}

/// <summary>Sources of <see cref="DiscordTimelineEntryDto"/>.</summary>
public static class DiscordTimelineSources
{
    public const string Rsi = "rsi";
    public const string Discord = "discord";
}

/// <summary>One entry of the combined RSI + Discord timeline of a citizen (spec § 10.4).</summary>
public sealed class DiscordTimelineEntryDto
{
    /// <summary><see cref="DiscordTimelineSources"/> value.</summary>
    public string Source { get; set; } = null!;

    /// <summary>change_events.ChangeType or discord_member_events.Type.</summary>
    public string Type { get; set; } = null!;

    /// <summary>Exact date when known, else the date it was observed.</summary>
    public DateTime At { get; set; }

    /// <summary>Earliest possible date, for a Discord event whose exact date is unknown.</summary>
    public DateTime? NotBefore { get; set; }

    public string? OrgSid { get; set; }
    public string? GuildId { get; set; }
    public string? GuildName { get; set; }
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
}

/// <summary>Answer of GET api/users/{handle}/discord: empty lists for a person without Discord data.</summary>
public sealed class DiscordUserProfileDto
{
    public IReadOnlyList<DiscordProfileAccountDto> Accounts { get; set; } = [];

    /// <summary>At most 100 entries, newest first.</summary>
    public IReadOnlyList<DiscordTimelineEntryDto> Timeline { get; set; } = [];
}

/// <summary>A guild mapped to an org, for the org page's DISCORD panel.</summary>
public sealed class DiscordOrgGuildDto
{
    public string GuildId { get; set; } = null!;
    public string Name { get; set; } = null!;
    public string? IconHash { get; set; }

    /// <summary>Active, non-bot members.</summary>
    public int ActiveMembers { get; set; }

    /// <summary>Active, non-bot members linked to at least one person.</summary>
    public int LinkedMembers { get; set; }

    public DateTime LastSyncAt { get; set; }

    /// <summary>The last accepted sync was complete (and did not trip the departure guard).</summary>
    public bool LastSyncComplete { get; set; }
}
```

- [ ] **Step 4: Write the profile service**

Create `src/Collector.Api/Services/Discord/DiscordProfileService.cs`:

```csharp
using Collector.Api.Dtos.Discord;
using Collector.Api.Errors;
using Collector.Data;
using Collector.Discord;
using Collector.Models;
using Microsoft.EntityFrameworkCore;

namespace Collector.Api.Services.Discord;

/// <summary>
/// The Discord side of a citizen's page (spec § 10.4) and of an org's page (spec § 11): the
/// Discord accounts linked to a person with their guilds and a combined RSI + Discord
/// timeline, and the guilds mapped to an org. Everything is read on request, so a guild
/// re-mapped to another org moves between the org pages at once.
/// </summary>
public sealed class DiscordProfileService(TrackerDbContext db)
{
    public const int MaxTimelineEntries = 100;

    /// <summary>The RSI changes the combined timeline keeps (spec § 10.4).</summary>
    private static readonly string[] RsiTimelineTypes =
        ["member_joined", "member_left", "rank_changed", "roles_changed", "handle_changed"];

    /// <summary>
    /// Linked accounts, their guilds and the combined timeline of the person known as
    /// <paramref name="handle"/> (regardless of case). Empty lists when the person has no
    /// Discord data; 404 when no source knows the handle.
    /// </summary>
    public async Task<DiscordUserProfileDto> GetUserProfileAsync(string handle, CancellationToken ct)
    {
        var wanted = handle.Trim();
        var (known, entity) = await ResolvePersonAsync(wanted, ct);
        if (!known) throw new NotFoundException($"Citoyen inconnu : « {wanted} ».");
        if (entity is null) return new DiscordUserProfileDto();

        var linkedIds = await db.EntityLinks.AsNoTracking()
            .Where(l => l.TrackedEntityId == entity.Id && l.Provider == LinkProviders.Discord)
            .Select(l => l.Value)
            .Distinct()
            .ToListAsync(ct);
        if (linkedIds.Count == 0) return new DiscordUserProfileDto();
        var accounts = await db.DiscordAccounts.AsNoTracking()
            .Where(a => linkedIds.Contains(a.DiscordUserId))
            .OrderBy(a => a.Username).ThenBy(a => a.DiscordUserId)
            .ToListAsync(ct);
        if (accounts.Count == 0) return new DiscordUserProfileDto();

        var accountIds = accounts.Select(a => a.DiscordUserId).ToList();
        var memberships = await db.DiscordMembers.AsNoTracking()
            .Where(m => accountIds.Contains(m.DiscordUserId))
            .ToListAsync(ct);
        var discordEvents = await db.DiscordMemberEvents.AsNoTracking()
            .Where(e => accountIds.Contains(e.DiscordUserId))
            .OrderByDescending(e => e.OccurredAt ?? e.ObservedAt).ThenByDescending(e => e.Id)
            .Take(MaxTimelineEntries)
            .ToListAsync(ct);
        var guildIds = memberships.Select(m => m.GuildId)
            .Concat(discordEvents.Where(e => e.GuildId != null).Select(e => e.GuildId!))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var guilds = (await db.DiscordGuilds.AsNoTracking()
                .Where(g => guildIds.Contains(g.GuildId))
                .Select(g => new { g.GuildId, g.Name, g.OrgSid })
                .ToListAsync(ct))
            .ToDictionary(g => g.GuildId, g => new GuildInfo(g.Name, g.OrgSid), StringComparer.Ordinal);
        var rolesByGuild = (await db.DiscordRoles.AsNoTracking().Where(r => guildIds.Contains(r.GuildId)).ToListAsync(ct))
            .GroupBy(r => r.GuildId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToDictionary(r => r.RoleId, StringComparer.Ordinal), StringComparer.Ordinal);

        var entries = await RsiTimelineAsync(entity, ct);
        foreach (var e in discordEvents)
        {
            var guild = e.GuildId is not null ? guilds.GetValueOrDefault(e.GuildId) : null;
            entries.Add((new DiscordTimelineEntryDto
            {
                Source = DiscordTimelineSources.Discord,
                Type = e.Type,
                At = e.OccurredAt ?? e.ObservedAt,
                NotBefore = e.OccurredAt is null ? e.NotBefore : null,
                OrgSid = guild?.OrgSid,
                GuildId = e.GuildId,
                GuildName = guild?.Name,
                OldValue = e.OldValue,
                NewValue = e.NewValue,
            }, e.Id));
        }

        return new DiscordUserProfileDto
        {
            Accounts = accounts.Select(a => new DiscordProfileAccountDto
            {
                DiscordUserId = a.DiscordUserId,
                Username = a.Username,
                GlobalName = a.GlobalName,
                Guilds = memberships
                    .Where(m => m.DiscordUserId == a.DiscordUserId)
                    .Select(m => new DiscordProfileGuildDto
                    {
                        GuildId = m.GuildId,
                        GuildName = guilds.GetValueOrDefault(m.GuildId)?.Name ?? m.GuildId,
                        OrgSid = guilds.GetValueOrDefault(m.GuildId)?.OrgSid,
                        Rank = rolesByGuild.TryGetValue(m.GuildId, out var roles)
                            ? DiscordRankResolver.Resolve(DiscordRoleLists.ParseRoleIds(m.RoleIdsJson), roles)?.Name
                            : null,
                        JoinedAt = m.JoinedAt,
                        LeftAt = m.LeftAt,
                        LastSeenAt = m.LastSeenAt,
                    })
                    .OrderBy(g => g.LeftAt is null ? 0 : 1)
                    .ThenBy(g => g.GuildName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(g => g.GuildId, StringComparer.Ordinal)
                    .ToList(),
            }).ToList(),
            Timeline = entries
                .OrderByDescending(x => x.Entry.At)
                .ThenBy(x => x.Entry.Source, StringComparer.Ordinal)
                .ThenByDescending(x => x.Id)
                .Take(MaxTimelineEntries)
                .Select(x => x.Entry)
                .ToList(),
        };
    }

    /// <summary>The guilds mapped to the org (SID regardless of case), by name; empty when none.</summary>
    public async Task<IReadOnlyList<DiscordOrgGuildDto>> GetOrgGuildsAsync(string sid, CancellationToken ct)
    {
        var orgSid = sid.Trim().ToUpperInvariant();
        var guilds = await db.DiscordGuilds.AsNoTracking().Where(g => g.OrgSid == orgSid).ToListAsync(ct);
        if (guilds.Count == 0) return [];

        var guildIds = guilds.Select(g => g.GuildId).ToList();
        var humans = from m in db.DiscordMembers.AsNoTracking()
                     join a in db.DiscordAccounts.AsNoTracking() on m.DiscordUserId equals a.DiscordUserId
                     where guildIds.Contains(m.GuildId) && m.LeftAt == null && !a.IsBot
                     select m;
        var active = await humans
            .GroupBy(m => m.GuildId)
            .Select(g => new { GuildId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.GuildId, x => x.Count, StringComparer.Ordinal, ct);
        var linked = await humans
            .Where(m => db.EntityLinks.Any(l => l.Provider == LinkProviders.Discord && l.Value == m.DiscordUserId))
            .GroupBy(m => m.GuildId)
            .Select(g => new { GuildId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.GuildId, x => x.Count, StringComparer.Ordinal, ct);

        return guilds
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.GuildId, StringComparer.Ordinal)
            .Select(g => new DiscordOrgGuildDto
            {
                GuildId = g.GuildId,
                Name = g.Name,
                IconHash = g.IconHash,
                ActiveMembers = active.GetValueOrDefault(g.GuildId),
                LinkedMembers = linked.GetValueOrDefault(g.GuildId),
                LastSyncAt = g.LastSyncAt,
                // LastCompleteSyncAt is the CollectedAt of the last complete, un-guarded sync.
                LastSyncComplete = g.LastCompleteSyncAt is not null && g.LastCompleteSyncAt == g.LastCollectedAt,
            })
            .ToList();
    }

    /// <summary>
    /// Resolves a handle as the links and memberships routes do, regardless of case: a citizen
    /// id from users, else from user_handle_history; the entity of that citizen id, else the
    /// entity whose current handle it is (never one bound to another citizen id). Known also
    /// when only an org roster has the handle.
    /// </summary>
    private async Task<(bool Known, TrackedEntity? Entity)> ResolvePersonAsync(string handle, CancellationToken ct)
    {
        var citizenId = await db.Users.AsNoTracking()
            .Where(u => EF.Functions.Collate(u.UserHandle, "NOCASE") == handle)
            .OrderByDescending(u => u.UpdatedAt)
            .Select(u => (int?)u.CitizenId)
            .FirstOrDefaultAsync(ct);
        citizenId ??= await db.UserHandleHistories.AsNoTracking()
            .Where(h => EF.Functions.Collate(h.UserHandle, "NOCASE") == handle)
            .OrderByDescending(h => h.LastSeen)
            .Select(h => (int?)h.CitizenId)
            .FirstOrDefaultAsync(ct);

        TrackedEntity? entity = null;
        if (citizenId is int cid)
            entity = await db.TrackedEntities.AsNoTracking().FirstOrDefaultAsync(e => e.CitizenId == cid, ct);
        entity ??= await db.TrackedEntities.AsNoTracking()
            .Where(e => e.CurrentHandle != null
                && EF.Functions.Collate(e.CurrentHandle, "NOCASE") == handle
                && (citizenId == null || e.CitizenId == null || e.CitizenId == citizenId))
            .OrderBy(e => e.Id)
            .FirstOrDefaultAsync(ct);

        if (entity is not null || citizenId is not null) return (true, entity);
        var inRoster = await db.OrganizationMembers.AsNoTracking()
            .AnyAsync(m => EF.Functions.Collate(m.UserHandle, "NOCASE") == handle, ct);
        return (inRoster, null);
    }

    /// <summary>
    /// RSI part of the timeline: the change_events of each handle of the person, an old handle
    /// only before the FirstSeen of the citizen's next handle (it may have been taken by
    /// someone else afterwards), the current handle without bound.
    /// </summary>
    private async Task<List<(DiscordTimelineEntryDto Entry, long Id)>> RsiTimelineAsync(TrackedEntity entity, CancellationToken ct)
    {
        var entries = new List<(DiscordTimelineEntryDto Entry, long Id)>();
        foreach (var (handle, before) in await HandleWindowsAsync(entity, ct))
        {
            var query = db.ChangeEvents.AsNoTracking()
                .Where(c => c.UserHandle == handle && RsiTimelineTypes.Contains(c.ChangeType));
            if (before is DateTime bound) query = query.Where(c => c.Timestamp < bound);
            var rows = await query
                .OrderByDescending(c => c.Timestamp).ThenByDescending(c => c.Id)
                .Take(MaxTimelineEntries)
                .Select(c => new { c.Id, c.ChangeType, c.Timestamp, c.OrgSid, c.OldValue, c.NewValue })
                .ToListAsync(ct);
            entries.AddRange(rows.Select(c => (new DiscordTimelineEntryDto
            {
                Source = DiscordTimelineSources.Rsi,
                Type = c.ChangeType,
                At = c.Timestamp,
                NotBefore = null,
                OrgSid = c.OrgSid,
                OldValue = c.OldValue,
                NewValue = c.NewValue,
            }, c.Id)));
        }
        return entries;
    }

    /// <summary>
    /// Each handle of the person with its upper bound: with a citizen id, every handle of
    /// user_handle_history (bounded by the next handle's FirstSeen) and the current handle of
    /// users (unbounded); without, CurrentHandle alone. A handle held twice keeps its widest window.
    /// </summary>
    private async Task<List<(string Handle, DateTime? Before)>> HandleWindowsAsync(TrackedEntity entity, CancellationToken ct)
    {
        var windows = new List<(string Handle, DateTime? Before)>();
        if (entity.CitizenId is int citizenId)
        {
            var history = await db.UserHandleHistories.AsNoTracking()
                .Where(h => h.CitizenId == citizenId)
                .OrderBy(h => h.FirstSeen).ThenBy(h => h.Id)
                .Select(h => new { h.UserHandle, h.FirstSeen })
                .ToListAsync(ct);
            for (var i = 0; i < history.Count; i++)
                windows.Add((history[i].UserHandle, i + 1 < history.Count ? history[i + 1].FirstSeen : (DateTime?)null));
            var current = await db.Users.AsNoTracking()
                .Where(u => u.CitizenId == citizenId)
                .Select(u => u.UserHandle)
                .FirstOrDefaultAsync(ct);
            if (current is not null) windows.Add((current, null));
            else if (history.Count == 0 && entity.CurrentHandle is not null) windows.Add((entity.CurrentHandle, null));
        }
        else if (entity.CurrentHandle is not null)
        {
            windows.Add((entity.CurrentHandle, null));
        }

        return windows
            .GroupBy(w => w.Handle, StringComparer.Ordinal)
            .Select(g => (Handle: g.Key, Before: g.Any(w => w.Before is null) ? null : g.Max(w => w.Before)))
            .ToList();
    }

    private sealed record GuildInfo(string Name, string? OrgSid);
}
```

- [ ] **Step 5: Add the routes and register the service**

In `src/Collector.Api/Controllers/DiscordRostersController.cs`, replace the end of the class:

```csharp
        => Ok(await queries.GetSyncsAsync(guildId, limit, ct));
}
```

with:

```csharp
        => Ok(await queries.GetSyncsAsync(guildId, limit, ct));

    /// <summary>The person's linked Discord accounts, their guilds and the combined timeline (spec § 10.4).</summary>
    [HttpGet("users/{handle}/discord")]
    public async Task<ActionResult<DiscordUserProfileDto>> GetUserDiscord(
        string handle, [FromServices] DiscordProfileService profiles, CancellationToken ct)
        => Ok(await profiles.GetUserProfileAsync(handle, ct));

    /// <summary>The guilds mapped to the org; an empty list when there is none.</summary>
    [HttpGet("organizations/{sid}/discord")]
    public async Task<ActionResult<IReadOnlyList<DiscordOrgGuildDto>>> GetOrgDiscordGuilds(
        string sid, [FromServices] DiscordProfileService profiles, CancellationToken ct)
        => Ok(await profiles.GetOrgGuildsAsync(sid, ct));
}
```

In `src/Collector.Api/Extensions/ServiceCollectionExtensions.cs`, method `AddApiServices`, replace:

```csharp
        services.AddHostedService<AudioOrphanSweeper>();
```

with:

```csharp
        services.AddHostedService<AudioOrphanSweeper>();

        // Discord cross profile of a citizen and guilds of an org (spec § 10.4, § 11).
        services.AddScoped<Collector.Api.Services.Discord.DiscordProfileService>();
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test src/Collector.Api.Tests --filter "FullyQualifiedName~DiscordProfileTests"`

Expected: `Passed!  - Failed:     0, Passed:     5, Skipped:     0, Total:     5`

- [ ] **Step 7: Check that nothing regressed and the new routes stay closed**

Run: `dotnet test src/Collector.Api.Tests --filter "FullyQualifiedName~DiscordReconciliationTests|FullyQualifiedName~DiscordRosterQuery|FullyQualifiedName~AuthorizationTests|FullyQualifiedName~UserOrganizationsTests|FullyQualifiedName~OrganizationDetailTests"`

Expected: `Passed!` with `Failed:     0` (`GET /api/users/x/discord` and `GET /api/organizations/x/discord` answer 401 to an anonymous caller and to a `discord:ingest` key; the existing users and organizations routes still resolve).

- [ ] **Step 8: Commit**

```bash
git add src/Collector.Api/Services/Discord/DiscordProfileService.cs \
        src/Collector.Api/Dtos/Discord/DiscordRosterDtos.cs \
        src/Collector.Api/Controllers/DiscordRostersController.cs \
        src/Collector.Api/Extensions/ServiceCollectionExtensions.cs \
        src/Collector.Api.Tests/Discord/DiscordProfileTests.cs
git commit -F - <<'EOF'
feat(api): show a citizen's discord accounts and an org's discord guilds

A citizen's page and an org's page are where Discord data meets the RSI
history. The profile resolves the person as the links and memberships
routes do, lists the linked accounts with their servers and ranks, and
merges RSI and Discord events into one timeline of at most 100 entries.
An old handle only contributes the events from before the citizen's next
handle, since RSI lets someone else take it afterwards. A person without
Discord data gets empty lists and a handle nobody knows gets a 404, so the
page can tell "nothing yet" from "nobody". An org lists the guilds mapped
to it, which follows a re-mapping immediately.

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
EOF
```
