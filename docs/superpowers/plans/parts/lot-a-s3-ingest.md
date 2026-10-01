### Task A9: Ingest DTOs and DiscordSyncValidator

**Files:**
- Create: `src/Collector.Api/Dtos/Discord/DiscordIngestDtos.cs`
- Create: `src/Collector.Api/Services/Discord/DiscordErrorCodes.cs`
- Create: `src/Collector.Api/Services/Discord/DiscordSyncValidator.cs`
- Test: `src/Collector.Api.Tests/Discord/DiscordSyncValidatorTests.cs`

**Interfaces:**
- Consumes:
  - `Collector.Discord.DiscordSnowflake.IsValid(string? s)` (CONTRACTS § 0).
  - `Collector.Discord.NormalizedSync`, `NormalizedRole`, `NormalizedMember` (CONTRACTS § 2), built with the parameter names given there.
  - `Collector.Models.DiscordSyncMethods.IsValid(string?)` and `DiscordSyncMethods.MemberSearch` (CONTRACTS § 1).
  - `Collector.Api.Errors.ValidationException(string message, string? code = null)` and `DomainException.Code` (CONTRACTS § 4).
- Produces:
  - `Collector.Api.Dtos.Discord`: `DiscordSyncRequest`, `DiscordSyncGuild`, `DiscordSyncCoverage`, `DiscordSyncRole`, `DiscordSyncMember`, `DiscordSyncEventCountsDto`, `DiscordSyncResponseDto`, exactly as CONTRACTS § 5.
  - `Collector.Api.Services.Discord.DiscordErrorCodes` (new, decided here): `InvalidSync = "invalid_sync"`, `EmptyCompleteSync = "empty_complete_sync"`, `StaleSync = "stale_sync"`, `GuildExcluded = "guild_excluded"`, `Busy = "busy"`.
  - `public static NormalizedSync DiscordSyncValidator.Normalize(string routeGuildId, DiscordSyncRequest r)` and its public bounds: `MaxGuildNameLength = 100`, `MaxRoleNameLength = 100`, `MaxNameLength = 32`, `MaxPluginVersionLength = 20`, `MaxCount = 1_000_000`, `MaxRolePosition = 1_000`, `MaxCollectionDurationMs = 1_800_000`, `MaxRoles = 250`, `MaxMembers = 50_000`, `MaxRoleIdsPerMember = 250`.
  - Decisions left open by CONTRACTS:
    - Text lengths are counted in Unicode scalar values (`EnumerateRunes`).
    - `guild.name`, `roles[].name`, `members[].username` and `pluginVersion` must be non-empty.
    - `nick` and `globalName` are trimmed **before** their length check.
    - A role id listed twice in `roles` is `invalid_sync`.
    - `color` accepts `#rrggbb` in either case and is stored lower-case.
    - The icon and color patterns end with `\z`, not `$` (a trailing `"\n"` is refused).
    - `UnknownRoleRefCount` counts one per member and distinct unknown role id, after de-duplication.
    - `DeclaredCollectedAt = collectedAt.UtcDateTime` (not truncated). `collectedAt` is never range-checked.
    - The `ProblemDetails.detail` texts are French: the plugin shows them.

- [ ] **Step 1: Write the failing test**

Create `src/Collector.Api.Tests/Discord/DiscordSyncValidatorTests.cs`:

```csharp
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Collector.Api.Dtos.Discord;
using Collector.Api.Errors;
using Collector.Api.Services.Discord;
using Collector.Discord;
using FluentAssertions;
using Xunit;

namespace Collector.Api.Tests.Discord;

/// <summary>
/// Spec § 7.3: the bounds of a plugin sync, its normalisation before the diff, and the
/// completeness the server recomputes instead of trusting the plugin's flag.
/// </summary>
public class DiscordSyncValidatorTests
{
    private const string GuildId = "123456789012345678";
    private const string RoleA = "200000000000000001";
    private const string RoleB = "200000000000000002";
    private const string UserA = "300000000000000001";
    private const string UserB = "300000000000000002";

    /// <summary>One code point outside the BMP: two UTF-16 units.</summary>
    private static readonly string Rocket = char.ConvertFromUtf32(0x1F680);

    private const string ValidJson = """
        {
          "pluginVersion": "1.0.0",
          "collectedAt": "2026-09-30T12:00:00Z",
          "collectionDurationMs": 84000,
          "sentByALaterPlugin": { "extra": [1, 2, 3] },
          "guild": { "id": "123456789012345678", "name": "Ma Corpo", "icon": null, "memberCount": null, "banner": "ignored" },
          "coverage": { "method": "member-search", "complete": true, "expectedCount": null, "collectedCount": 1 },
          "roles": [],
          "members": [
            { "userId": "300000000000000001", "username": "pilote42", "globalName": null, "nick": null,
              "roleIds": [], "joinedAt": null, "bot": false, "avatar": "ignored" }
          ]
        }
        """;

    private static DiscordSyncMember Member(string userId, string username = "pilote42", IReadOnlyList<string>? roleIds = null,
        string? nick = null, string? globalName = null, DateTimeOffset? joinedAt = null) =>
        new(userId, username, globalName, nick, roleIds ?? [], joinedAt, false);

    private static DiscordSyncRequest Valid(params DiscordSyncMember[] members)
    {
        DiscordSyncMember[] list = members.Length > 0 ? members : [Member(UserA), Member(UserB, username: "pilote43")];
        return new DiscordSyncRequest(
            PluginVersion: "1.0.0",
            CollectedAt: new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero),
            CollectionDurationMs: 84_000,
            Guild: new DiscordSyncGuild(GuildId, "Ma Corpo", "a_" + new string('1', 32), 1234),
            Coverage: new DiscordSyncCoverage("member-search", true, list.Length, list.Length),
            Roles:
            [
                new DiscordSyncRole(RoleA, "Officier", 12, "#E67E22", true, false),
                new DiscordSyncRole(RoleB, "Pilote", 5, null, false, false),
            ],
            Members: list);
    }

    [Fact]
    public void ValidSync_IsNormalised()
    {
        var joinedAt = new DateTimeOffset(2025, 3, 14, 22, 11, 5, 123, TimeSpan.FromHours(2));

        var sync = DiscordSyncValidator.Normalize(GuildId, Valid(
            Member(UserA, roleIds: [RoleB, RoleA, RoleB], nick: "  [CORP] Pilote42  ", globalName: "   ", joinedAt: joinedAt),
            Member(UserB, username: "pilote43", nick: "", globalName: " Pilote ")));

        sync.GuildId.Should().Be(GuildId);
        sync.GuildName.Should().Be("Ma Corpo");
        sync.IconHash.Should().Be("a_" + new string('1', 32));
        sync.MemberCount.Should().Be(1234);
        sync.PluginVersion.Should().Be("1.0.0");
        sync.CollectionDuration.Should().Be(TimeSpan.FromSeconds(84));
        sync.DeclaredCollectedAt.Should().Be(new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc));
        sync.DeclaredCollectedAt.Kind.Should().Be(DateTimeKind.Utc);
        sync.CollectedCount.Should().Be(2);
        sync.UnknownRoleRefCount.Should().Be(0);
        sync.Roles.Should().Equal(
            new NormalizedRole(RoleA, "Officier", 12, "#e67e22", true, false),
            new NormalizedRole(RoleB, "Pilote", 5, null, false, false));

        var a = sync.Members[0];
        a.UserId.Should().Be(UserA);
        a.RoleIds.Should().Equal(RoleA, RoleB);
        a.Nick.Should().Be("[CORP] Pilote42");
        a.GlobalName.Should().BeNull("a blank global name is no global name");
        a.JoinedAt.Should().Be(new DateTime(2025, 3, 14, 20, 11, 5, DateTimeKind.Utc), "UTC, truncated to the second");
        a.JoinedAt!.Value.Kind.Should().Be(DateTimeKind.Utc);
        var b = sync.Members[1];
        b.Nick.Should().BeNull();
        b.GlobalName.Should().Be("Pilote");
        b.JoinedAt.Should().BeNull();
        b.IsBot.Should().BeFalse();
    }

    [Fact]
    public void UnknownRoleReferences_AreDropped_AndCounted()
    {
        const string deleted = "200000000000000099";

        var sync = DiscordSyncValidator.Normalize(GuildId, Valid(
            Member(UserA, roleIds: [RoleA, deleted, deleted]),
            Member(UserB, username: "pilote43", roleIds: [deleted])));

        sync.Members[0].RoleIds.Should().Equal(RoleA);
        sync.Members[1].RoleIds.Should().BeEmpty();
        sync.UnknownRoleRefCount.Should().Be(2, "one per member that still referenced the deleted role");
    }

    [Theory]
    [InlineData(true, "member-search", 2, true)]
    [InlineData(true, "member-search", 3, false)]
    [InlineData(true, "role-members", 2, false)]
    [InlineData(true, "cache", 2, false)]
    [InlineData(false, "member-search", 2, false)]
    public void Completeness_IsRecomputedOnTheRawMessage(bool declared, string method, int expected, bool complete)
    {
        var request = Valid() with { Coverage = new DiscordSyncCoverage(method, declared, expected, 2) };

        var sync = DiscordSyncValidator.Normalize(GuildId, request);

        sync.IsComplete.Should().Be(complete);
        sync.DeclaredComplete.Should().Be(declared);
        sync.Method.Should().Be(method);
        sync.ExpectedCount.Should().Be(expected);
    }

    [Fact]
    public void Completeness_WithoutAnExpectedCount_IsPartial()
    {
        var request = Valid() with { Coverage = new DiscordSyncCoverage("member-search", true, null, 2) };

        var sync = DiscordSyncValidator.Normalize(GuildId, request);

        sync.IsComplete.Should().BeFalse("nothing proves the list is whole");
        sync.ExpectedCount.Should().BeNull();
    }

    [Fact]
    public void CompleteSync_WithoutMembers_IsRefusedWithItsOwnCode()
    {
        var request = Valid() with { Members = [], Coverage = new DiscordSyncCoverage("member-search", true, 0, 0) };

        var act = () => DiscordSyncValidator.Normalize(GuildId, request);

        act.Should().Throw<ValidationException>().Which.Code.Should().Be("empty_complete_sync");
    }

    [Fact]
    public void PartialSync_WithoutMembers_IsAccepted()
    {
        var request = Valid() with { Members = [], Coverage = new DiscordSyncCoverage("role-members", false, null, 0) };

        DiscordSyncValidator.Normalize(GuildId, request).Members.Should().BeEmpty();
    }

    [Theory]
    [InlineData("route-not-snowflake")]
    [InlineData("guild-id-not-snowflake")]
    [InlineData("guild-id-mismatch")]
    [InlineData("guild-name-empty")]
    [InlineData("guild-name-too-long")]
    [InlineData("icon-not-a-hash")]
    [InlineData("icon-upper-case")]
    [InlineData("icon-trailing-newline")]
    [InlineData("member-count-negative")]
    [InlineData("member-count-too-large")]
    [InlineData("plugin-version-empty")]
    [InlineData("plugin-version-too-long")]
    [InlineData("duration-negative")]
    [InlineData("duration-over-30-minutes")]
    [InlineData("method-unknown")]
    [InlineData("expected-count-negative")]
    [InlineData("expected-count-too-large")]
    [InlineData("too-many-roles")]
    [InlineData("role-id-not-snowflake")]
    [InlineData("duplicate-role-id")]
    [InlineData("role-name-empty")]
    [InlineData("role-name-too-long")]
    [InlineData("role-position-negative")]
    [InlineData("role-position-too-large")]
    [InlineData("role-color-named")]
    [InlineData("role-color-short")]
    [InlineData("too-many-members")]
    [InlineData("user-id-not-snowflake")]
    [InlineData("duplicate-user-id")]
    [InlineData("username-empty")]
    [InlineData("username-too-long")]
    [InlineData("nick-too-long")]
    [InlineData("global-name-too-long")]
    [InlineData("too-many-role-ids")]
    [InlineData("role-ref-not-snowflake")]
    [InlineData("null-guild")]
    [InlineData("null-coverage")]
    [InlineData("null-roles")]
    [InlineData("null-members")]
    [InlineData("null-role")]
    [InlineData("null-member")]
    [InlineData("null-role-ids")]
    [InlineData("null-user-id")]
    [InlineData("null-username")]
    [InlineData("null-guild-name")]
    [InlineData("null-plugin-version")]
    [InlineData("null-method")]
    [InlineData("null-role-name")]
    public void InvalidSync_IsRefusedAsInvalidSync_NeverWithANullReference(string rule)
    {
        var (route, request) = Broken(rule);

        var act = () => DiscordSyncValidator.Normalize(route, request);

        act.Should().Throw<ValidationException>().Which.Code.Should().Be("invalid_sync");
    }

    [Fact]
    public void NamesOf32CodePoints_AreAccepted_EvenAsEmoji()
    {
        var emoji = string.Concat(Enumerable.Repeat(Rocket, 32));   // 64 UTF-16 units
        var mixed = new string('x', 31) + Rocket;                    // 32 code points, 33 units

        var sync = DiscordSyncValidator.Normalize(GuildId, Valid(
            Member(UserA, username: emoji, nick: emoji, globalName: emoji),
            Member(UserB, username: mixed, nick: mixed, globalName: mixed)));

        sync.Members[0].Username.Should().Be(emoji);
        sync.Members[0].Nick.Should().Be(emoji);
        sync.Members[0].GlobalName.Should().Be(emoji);
        sync.Members[1].Username.Should().Be(mixed);
    }

    [Theory]
    [InlineData("username")]
    [InlineData("nick")]
    [InlineData("globalName")]
    public void NamesOf33CodePoints_AreRefused(string field)
    {
        var name = string.Concat(Enumerable.Repeat(Rocket, 33));
        var member = field switch
        {
            "username" => Member(UserA, username: name),
            "nick" => Member(UserA, nick: name),
            _ => Member(UserA, globalName: name),
        };

        var act = () => DiscordSyncValidator.Normalize(GuildId, Valid(member));

        act.Should().Throw<ValidationException>().Which.Code.Should().Be("invalid_sync");
    }

    [Theory]
    [InlineData("2000-01-01T00:00:00+00:00")]
    [InlineData("2100-01-01T00:00:00+00:00")]
    [InlineData("2026-09-25T08:00:00-11:00")]
    public void PluginClock_IsOnlyRecorded_NeverValidated(string collectedAt)
    {
        var value = DateTimeOffset.Parse(collectedAt, CultureInfo.InvariantCulture);

        var sync = DiscordSyncValidator.Normalize(GuildId, Valid() with { CollectedAt = value });

        sync.DeclaredCollectedAt.Should().Be(value.UtcDateTime);
        sync.CollectionDuration.Should().Be(TimeSpan.FromSeconds(84), "the server dates a sync from its own clock and this duration");
    }

    [Fact]
    public void Json_WithUnknownFields_AndNullOptionalFields_IsAccepted()
    {
        var request = JsonSerializer.Deserialize<DiscordSyncRequest>(ValidJson, JsonSerializerOptions.Web)!;

        var sync = DiscordSyncValidator.Normalize(GuildId, request);

        sync.IconHash.Should().BeNull();
        sync.MemberCount.Should().BeNull();
        sync.ExpectedCount.Should().BeNull();
        sync.IsComplete.Should().BeFalse("without an expected count nothing proves the list is whole");
        var member = sync.Members.Should().ContainSingle().Which;
        member.GlobalName.Should().BeNull();
        member.Nick.Should().BeNull();
        member.JoinedAt.Should().BeNull();
    }

    [Theory]
    [InlineData("roles", true)]
    [InlineData("roles", false)]
    [InlineData("members", true)]
    [InlineData("members", false)]
    public void Json_WithoutARequiredArray_IsInvalidSync_NotANullReference(string field, bool presentAsNull)
    {
        var node = JsonNode.Parse(ValidJson)!.AsObject();
        if (presentAsNull) node[field] = null;
        else node.Remove(field);
        var request = node.Deserialize<DiscordSyncRequest>(JsonSerializerOptions.Web)!;

        var act = () => DiscordSyncValidator.Normalize(GuildId, request);

        act.Should().Throw<ValidationException>().Which.Code.Should().Be("invalid_sync");
    }

    private static (string Route, DiscordSyncRequest Request) Broken(string rule)
    {
        var r = Valid();
        var member = r.Members[0];
        var role = r.Roles[0];
        var name33 = new string('x', 33);
        return rule switch
        {
            "route-not-snowflake" => ("12345", r),
            "guild-id-not-snowflake" => (GuildId, r with { Guild = r.Guild with { Id = "12" } }),
            "guild-id-mismatch" => (GuildId, r with { Guild = r.Guild with { Id = "123456789012345679" } }),
            "guild-name-empty" => (GuildId, r with { Guild = r.Guild with { Name = "" } }),
            "guild-name-too-long" => (GuildId, r with { Guild = r.Guild with { Name = new string('x', 101) } }),
            "icon-not-a-hash" => (GuildId, r with { Guild = r.Guild with { Icon = "../../avatars/x.png" } }),
            "icon-upper-case" => (GuildId, r with { Guild = r.Guild with { Icon = new string('A', 32) } }),
            "icon-trailing-newline" => (GuildId, r with { Guild = r.Guild with { Icon = new string('a', 32) + "\n" } }),
            "member-count-negative" => (GuildId, r with { Guild = r.Guild with { MemberCount = -1 } }),
            "member-count-too-large" => (GuildId, r with { Guild = r.Guild with { MemberCount = 1_000_001 } }),
            "plugin-version-empty" => (GuildId, r with { PluginVersion = "" }),
            "plugin-version-too-long" => (GuildId, r with { PluginVersion = new string('1', 21) }),
            "duration-negative" => (GuildId, r with { CollectionDurationMs = -1 }),
            "duration-over-30-minutes" => (GuildId, r with { CollectionDurationMs = 1_800_001 }),
            "method-unknown" => (GuildId, r with { Coverage = r.Coverage with { Method = "bot" } }),
            "expected-count-negative" => (GuildId, r with { Coverage = r.Coverage with { ExpectedCount = -1 } }),
            "expected-count-too-large" => (GuildId, r with { Coverage = r.Coverage with { ExpectedCount = 1_000_001 } }),
            "too-many-roles" => (GuildId, r with
            {
                Roles = Enumerable.Range(0, 251).Select(i => role with { Id = $"2000000000000{i:D5}" }).ToList(),
            }),
            "role-id-not-snowflake" => (GuildId, r with { Roles = [role with { Id = "officer" }] }),
            "duplicate-role-id" => (GuildId, r with { Roles = [role, role with { Name = "Copie" }] }),
            "role-name-empty" => (GuildId, r with { Roles = [role with { Name = "" }] }),
            "role-name-too-long" => (GuildId, r with { Roles = [role with { Name = new string('x', 101) }] }),
            "role-position-negative" => (GuildId, r with { Roles = [role with { Position = -1 }] }),
            "role-position-too-large" => (GuildId, r with { Roles = [role with { Position = 1_001 }] }),
            "role-color-named" => (GuildId, r with { Roles = [role with { Color = "orange" }] }),
            "role-color-short" => (GuildId, r with { Roles = [role with { Color = "#fff" }] }),
            "too-many-members" => (GuildId, r with
            {
                Members = Enumerable.Range(0, 50_001).Select(i => member with { UserId = $"3{i:D17}" }).ToList(),
            }),
            "user-id-not-snowflake" => (GuildId, r with { Members = [member with { UserId = "300" }] }),
            "duplicate-user-id" => (GuildId, r with { Members = [member, member with { Username = "copie" }] }),
            "username-empty" => (GuildId, r with { Members = [member with { Username = "" }] }),
            "username-too-long" => (GuildId, r with { Members = [member with { Username = name33 }] }),
            "nick-too-long" => (GuildId, r with { Members = [member with { Nick = name33 }] }),
            "global-name-too-long" => (GuildId, r with { Members = [member with { GlobalName = name33 }] }),
            "too-many-role-ids" => (GuildId, r with { Members = [member with { RoleIds = Enumerable.Repeat(RoleA, 251).ToList() }] }),
            "role-ref-not-snowflake" => (GuildId, r with { Members = [member with { RoleIds = ["admin"] }] }),
            "null-guild" => (GuildId, r with { Guild = null! }),
            "null-coverage" => (GuildId, r with { Coverage = null! }),
            "null-roles" => (GuildId, r with { Roles = null! }),
            "null-members" => (GuildId, r with { Members = null! }),
            "null-role" => (GuildId, r with { Roles = [null!] }),
            "null-member" => (GuildId, r with { Members = [null!] }),
            "null-role-ids" => (GuildId, r with { Members = [member with { RoleIds = null! }] }),
            "null-user-id" => (GuildId, r with { Members = [member with { UserId = null! }] }),
            "null-username" => (GuildId, r with { Members = [member with { Username = null! }] }),
            "null-guild-name" => (GuildId, r with { Guild = r.Guild with { Name = null! } }),
            "null-plugin-version" => (GuildId, r with { PluginVersion = null! }),
            "null-method" => (GuildId, r with { Coverage = r.Coverage with { Method = null! } }),
            "null-role-name" => (GuildId, r with { Roles = [role with { Name = null! }] }),
            _ => throw new ArgumentOutOfRangeException(nameof(rule), rule, null),
        };
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test src/Collector.Api.Tests --filter "FullyQualifiedName~DiscordSyncValidatorTests"`

Expected: the build fails with `error CS0234: The type or namespace name 'Discord' does not exist in the namespace 'Collector.Api.Services'` and `error CS0246: The type or namespace name 'DiscordSyncRequest' could not be found`.

- [ ] **Step 3: Write the minimal implementation**

Create `src/Collector.Api/Dtos/Discord/DiscordIngestDtos.cs`:

```csharp
namespace Collector.Api.Dtos.Discord;

/// <summary>
/// Body of <c>POST api/ingest/discord/guilds/{guildId}/syncs</c>, sent by the Vencord plugin
/// (spec § 7.1). Snowflakes travel as strings, since JavaScript numbers cannot hold them.
/// <c>CollectedAt</c> is the plugin's clock and only informative. Unknown JSON fields are
/// ignored, so a later plugin may send more than this.
/// </summary>
public sealed record DiscordSyncRequest(
    string PluginVersion, DateTimeOffset CollectedAt, long CollectionDurationMs,
    DiscordSyncGuild Guild, DiscordSyncCoverage Coverage,
    IReadOnlyList<DiscordSyncRole> Roles, IReadOnlyList<DiscordSyncMember> Members);

/// <summary>The guild as the client saw it; <c>Icon</c> is Discord's icon hash.</summary>
public sealed record DiscordSyncGuild(string Id, string Name, string? Icon, int? MemberCount);

/// <summary>
/// How the members were collected, and whether the plugin believes the list is whole. The
/// server recomputes completeness and never trusts <c>Complete</c> alone.
/// </summary>
public sealed record DiscordSyncCoverage(string Method, bool Complete, int? ExpectedCount, int CollectedCount);

/// <summary>A guild role. The plugin always sends the whole list, without @everyone.</summary>
public sealed record DiscordSyncRole(string Id, string Name, int Position, string? Color, bool Hoist, bool Managed);

/// <summary>One member, read from a member-search response or a gateway chunk of this collection.</summary>
public sealed record DiscordSyncMember(
    string UserId, string Username, string? GlobalName, string? Nick,
    IReadOnlyList<string> RoleIds, DateTimeOffset? JoinedAt, bool Bot);

/// <summary>Events created by one sync, by kind. <c>NameChanged</c> covers username and global name.</summary>
public sealed class DiscordSyncEventCountsDto
{
    public int Joined { get; set; }
    public int Left { get; set; }
    public int Rejoined { get; set; }
    public int RolesChanged { get; set; }
    public int NickChanged { get; set; }
    public int NameChanged { get; set; }
}

/// <summary>Answer to an accepted sync (spec § 7.2). <c>OrgSid</c> is null for a guild tied to no org.</summary>
public sealed class DiscordSyncResponseDto
{
    public long SyncId { get; set; }
    public bool IsBaseline { get; set; }
    public bool IsComplete { get; set; }
    public bool DepartureGuardTripped { get; set; }
    public string? OrgSid { get; set; }
    public int MembersReceived { get; set; }
    public int MembersOptedOut { get; set; }
    public int UnknownRoleRefs { get; set; }
    public DiscordSyncEventCountsDto Events { get; set; } = new();
}
```

Create `src/Collector.Api/Services/Discord/DiscordErrorCodes.cs`:

```csharp
namespace Collector.Api.Services.Discord;

/// <summary>
/// Machine-readable <c>code</c> values of the Discord ProblemDetails (CONTRACTS § 4). The
/// plugin switches on them, so they never change once shipped.
/// </summary>
public static class DiscordErrorCodes
{
    /// <summary>400: the body breaks a rule of spec § 7.3.</summary>
    public const string InvalidSync = "invalid_sync";

    /// <summary>400: <c>complete: true</c> with no member, which would make everyone leave.</summary>
    public const string EmptyCompleteSync = "empty_complete_sync";

    /// <summary>409: a sync of this guild was accepted after this collection started.</summary>
    public const string StaleSync = "stale_sync";

    /// <summary>409: the guild is in discord_guild_optouts.</summary>
    public const string GuildExcluded = "guild_excluded";

    /// <summary>503: the Discord write gate or tracker.db stayed busy.</summary>
    public const string Busy = "busy";
}
```

Create `src/Collector.Api/Services/Discord/DiscordSyncValidator.cs`:

```csharp
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Collector.Api.Dtos.Discord;
using Collector.Api.Errors;
using Collector.Discord;
using Collector.Models;

namespace Collector.Api.Services.Discord;

/// <summary>
/// Checks a plugin sync against spec § 7.3 and turns it into the <see cref="NormalizedSync"/>
/// the diff works on. SQLite does not enforce TEXT(n), so every bound is checked here, and a
/// failure is a 400 before anything is written. Text lengths are counted in Unicode scalar
/// values, the unit Discord counts in: a nick of 32 emoji is valid although it takes 64 UTF-16
/// units. Nothing here reads a clock, because the server dates a sync itself.
/// </summary>
public static class DiscordSyncValidator
{
    public const int MaxGuildNameLength = 100;
    public const int MaxRoleNameLength = 100;

    /// <summary>Bound of username, global name and nick.</summary>
    public const int MaxNameLength = 32;

    public const int MaxPluginVersionLength = 20;

    /// <summary>Bound of <c>memberCount</c> and <c>expectedCount</c>.</summary>
    public const int MaxCount = 1_000_000;

    public const int MaxRolePosition = 1_000;

    /// <summary>30 minutes.</summary>
    public const long MaxCollectionDurationMs = 1_800_000;

    public const int MaxRoles = 250;
    public const int MaxMembers = 50_000;
    public const int MaxRoleIdsPerMember = 250;

    // \z rather than $: in .NET, $ also matches before a final "\n".
    private static readonly Regex IconHash = new(@"^(a_)?[0-9a-f]{32}\z", RegexOptions.Compiled);
    private static readonly Regex HexColor = new(@"^#[0-9a-fA-F]{6}\z", RegexOptions.Compiled);

    /// <summary>
    /// Validates <paramref name="r"/> for the guild of the URL and normalises it:
    /// <list type="bullet">
    /// <item>dates are converted to UTC, and <c>JoinedAt</c> is truncated to the second;</item>
    /// <item>nick and global name are trimmed, and a blank value reads as null;</item>
    /// <item>role ids are sorted and de-duplicated;</item>
    /// <item>references to roles absent from the payload are dropped and counted.</item>
    /// </list>
    /// Completeness is recomputed on the raw member count, before opt-outs are removed.
    /// </summary>
    /// <exception cref="ValidationException">
    /// Code <c>invalid_sync</c>, or <c>empty_complete_sync</c> for a sync declared complete
    /// without any member.
    /// </exception>
    public static NormalizedSync Normalize(string routeGuildId, DiscordSyncRequest r)
    {
        if (!DiscordSnowflake.IsValid(routeGuildId)) Fail("guildId (URL) : snowflake attendu.");
        if (r is null) Fail("Corps de requête absent.");
        if (r.Guild is null) Fail("guild est obligatoire.");
        if (r.Coverage is null) Fail("coverage est obligatoire.");
        if (r.Roles is null) Fail("roles est obligatoire.");
        if (r.Members is null) Fail("members est obligatoire.");

        var guild = r.Guild;
        if (!DiscordSnowflake.IsValid(guild.Id)) Fail("guild.id : snowflake attendu.");
        if (guild.Id != routeGuildId) Fail("guild.id ne correspond pas au serveur de l'URL.");
        RequireText(guild.Name, MaxGuildNameLength, "guild.name");
        if (guild.Icon is not null && !IconHash.IsMatch(guild.Icon)) Fail("guild.icon : hash d'icône Discord attendu.");
        RequireCount(guild.MemberCount, "guild.memberCount");

        RequireText(r.PluginVersion, MaxPluginVersionLength, "pluginVersion");
        if (r.CollectionDurationMs is < 0 or > MaxCollectionDurationMs)
            Fail($"collectionDurationMs : entre 0 et {MaxCollectionDurationMs}.");

        var coverage = r.Coverage;
        if (!DiscordSyncMethods.IsValid(coverage.Method))
            Fail("coverage.method : member-search, role-members ou cache.");
        RequireCount(coverage.ExpectedCount, "coverage.expectedCount");

        if (r.Roles.Count > MaxRoles) Fail($"roles : {MaxRoles} au plus.");
        if (r.Members.Count > MaxMembers) Fail($"members : {MaxMembers} au plus.");
        if (coverage.Complete && r.Members.Count == 0)
            throw new ValidationException("Envoi déclaré complet sans aucun membre.", DiscordErrorCodes.EmptyCompleteSync);

        var roles = NormalizeRoles(r.Roles);
        var knownRoleIds = roles.Select(role => role.RoleId).ToHashSet(StringComparer.Ordinal);
        var members = new List<NormalizedMember>(r.Members.Count);
        var userIds = new HashSet<string>(StringComparer.Ordinal);
        var unknownRoleRefs = 0;
        for (var i = 0; i < r.Members.Count; i++)
        {
            var member = NormalizeMember(r.Members[i], i, knownRoleIds, out var unknown);
            if (!userIds.Add(member.UserId)) Fail($"members[{i}].userId : membre en double ({member.UserId}).");
            unknownRoleRefs += unknown;
            members.Add(member);
        }

        // Never the plugin's flag alone: only a member search that returned exactly the
        // announced number of members proves the list is whole.
        var isComplete = coverage.Complete
            && coverage.Method == DiscordSyncMethods.MemberSearch
            && coverage.ExpectedCount == r.Members.Count;

        return new NormalizedSync(
            GuildId: guild.Id,
            GuildName: guild.Name,
            IconHash: guild.Icon,
            MemberCount: guild.MemberCount,
            Method: coverage.Method,
            DeclaredComplete: coverage.Complete,
            IsComplete: isComplete,
            ExpectedCount: coverage.ExpectedCount,
            CollectedCount: r.Members.Count,
            PluginVersion: r.PluginVersion,
            DeclaredCollectedAt: r.CollectedAt.UtcDateTime,
            CollectionDuration: TimeSpan.FromMilliseconds(r.CollectionDurationMs),
            Roles: roles,
            Members: members,
            UnknownRoleRefCount: unknownRoleRefs);
    }

    private static List<NormalizedRole> NormalizeRoles(IReadOnlyList<DiscordSyncRole> source)
    {
        var roles = new List<NormalizedRole>(source.Count);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < source.Count; i++)
        {
            var role = source[i];
            if (role is null) Fail($"roles[{i}] est vide.");
            if (!DiscordSnowflake.IsValid(role.Id)) Fail($"roles[{i}].id : snowflake attendu.");
            if (!ids.Add(role.Id)) Fail($"roles[{i}].id : rôle en double ({role.Id}).");
            if (string.IsNullOrEmpty(role.Name)) Fail($"roles[{i}].name est obligatoire.");
            if (TooLong(role.Name, MaxRoleNameLength)) Fail($"roles[{i}].name : {MaxRoleNameLength} caractères au plus.");
            if (role.Position is < 0 or > MaxRolePosition) Fail($"roles[{i}].position : entre 0 et {MaxRolePosition}.");
            if (role.Color is not null && !HexColor.IsMatch(role.Color)) Fail($"roles[{i}].color : #rrggbb attendu.");
            roles.Add(new NormalizedRole(role.Id, role.Name, role.Position, role.Color?.ToLowerInvariant(), role.Hoist, role.Managed));
        }
        return roles;
    }

    private static NormalizedMember NormalizeMember(
        DiscordSyncMember? m, int i, IReadOnlySet<string> knownRoleIds, out int unknownRoleRefs)
    {
        if (m is null) Fail($"members[{i}] est vide.");
        if (!DiscordSnowflake.IsValid(m.UserId)) Fail($"members[{i}].userId : snowflake attendu.");
        if (string.IsNullOrEmpty(m.Username)) Fail($"members[{i}].username est obligatoire.");
        if (TooLong(m.Username, MaxNameLength)) Fail($"members[{i}].username : {MaxNameLength} caractères au plus.");
        var globalName = Clean(m.GlobalName);
        if (globalName is not null && TooLong(globalName, MaxNameLength))
            Fail($"members[{i}].globalName : {MaxNameLength} caractères au plus.");
        var nick = Clean(m.Nick);
        if (nick is not null && TooLong(nick, MaxNameLength))
            Fail($"members[{i}].nick : {MaxNameLength} caractères au plus.");
        if (m.RoleIds is null) Fail($"members[{i}].roleIds est obligatoire.");
        if (m.RoleIds.Count > MaxRoleIdsPerMember) Fail($"members[{i}].roleIds : {MaxRoleIdsPerMember} au plus.");

        var distinct = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var roleId in m.RoleIds)
        {
            if (!DiscordSnowflake.IsValid(roleId)) Fail($"members[{i}].roleIds : snowflake attendu.");
            distinct.Add(roleId);
        }

        // A role missing from the payload was deleted during the collection: not an error.
        var roleIds = new List<string>(distinct.Count);
        unknownRoleRefs = 0;
        foreach (var roleId in distinct)
        {
            if (knownRoleIds.Contains(roleId)) roleIds.Add(roleId);
            else unknownRoleRefs++;
        }

        return new NormalizedMember(
            m.UserId, m.Username, globalName, nick, roleIds,
            m.JoinedAt is { } joinedAt ? TruncateToSecond(joinedAt.UtcDateTime) : null,
            m.Bot);
    }

    private static void RequireText(string? value, int max, string field)
    {
        if (string.IsNullOrEmpty(value)) Fail($"{field} est obligatoire.");
        if (TooLong(value, max)) Fail($"{field} : {max} caractères au plus.");
    }

    private static void RequireCount(int? value, string field)
    {
        if (value is < 0 or > MaxCount) Fail($"{field} : entre 0 et {MaxCount}.");
    }

    /// <summary>Length in Unicode scalar values, the unit Discord counts in (an emoji is one).</summary>
    private static bool TooLong(string value, int max)
    {
        if (value.Length <= max) return false;   // never more scalar values than UTF-16 units
        var count = 0;
        foreach (var _ in value.EnumerateRunes())
        {
            if (++count > max) return true;
        }
        return false;
    }

    /// <summary>Trims a display name; a blank one reads as absent.</summary>
    private static string? Clean(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static DateTime TruncateToSecond(DateTime utc) =>
        new(utc.Ticks - utc.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);

    [DoesNotReturn]
    private static void Fail(string detail) => throw new ValidationException(detail, DiscordErrorCodes.InvalidSync);
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test src/Collector.Api.Tests --filter "FullyQualifiedName~DiscordSyncValidatorTests"`

Expected: `Passed!  - Failed:     0, Passed:    70, Skipped:     0, Total:    70`

- [ ] **Step 5: Commit**

```bash
git add src/Collector.Api/Dtos/Discord/DiscordIngestDtos.cs \
  src/Collector.Api/Services/Discord/DiscordErrorCodes.cs \
  src/Collector.Api/Services/Discord/DiscordSyncValidator.cs \
  src/Collector.Api.Tests/Discord/DiscordSyncValidatorTests.cs
git commit -m "$(cat <<'EOF'
feat(api): the server validates and normalises Discord syncs before any write

SQLite does not enforce TEXT(n), so every bound of spec § 7.3 is checked in
code. A malformed sync from the public ingest route is then a 400 invalid_sync,
never a 500 or a truncated row. Names are measured in Unicode scalar values,
as Discord counts them, so a 32-emoji nick is accepted. Completeness is
recomputed from the raw message because the plugin's flag alone cannot be
trusted to prove that departures are real.

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
EOF
)"
```

### Task A10: DiscordWriteGate and DiscordIngestGateFilter

**Files:**
- Create: `src/Collector.Api/Services/Discord/DiscordWriteGate.cs`
- Create: `src/Collector.Api/Services/Discord/DiscordIngestGateFilter.cs`
- Modify: `src/Collector.Api/Extensions/ServiceCollectionExtensions.cs` (method `AddApiServices`, after `services.AddScoped<StatsService>();`)
- Test: `src/Collector.Api.Tests/Discord/DiscordWriteGateTests.cs`
- Test: `src/Collector.Api.Tests/Discord/DiscordIngestGateFilterTests.cs`

**Interfaces:**
- Consumes:
  - `Collector.Data.Repositories.IDiscordRosterRepository.IsGuildExcludedAsync(string guildId, CancellationToken ct = default)`, and the other members for the stub: `GetLastSyncReceivedAtAsync`, `LoadSnapshotAsync`, `GetOptedOutAsync`, `ApplyAsync`, plus `DiscordSyncWrite` and `DiscordSyncResult` (CONTRACTS § 3). Registered as scoped by the repository task.
  - `Collector.Discord.DiscordSnowflake.IsValid` and `Collector.Discord.RosterSnapshot` (CONTRACTS § 2).
  - `ValidationException(string, string?)`, `ConflictException(string, string?)`, `ServiceUnavailableException(string message, int retryAfterSeconds, string? code = "busy")` and `DomainException.StatusCode` / `Code` (CONTRACTS § 4).
  - `DiscordErrorCodes` (Task A9).
- Produces:
  - `public sealed class DiscordWriteGate`, registered as a singleton in `AddApiServices`, with:
    - `Task<IDisposable?> TryEnterAsync(TimeSpan timeout, CancellationToken ct)`, which returns null on timeout;
    - `Task<IDisposable> EnterAsync(CancellationToken ct)`.
    - Disposing a lease releases the gate **once**, however many times it is disposed.
  - `public sealed class DiscordIngestGateFilter : IAsyncResourceFilter`:
    - constructor `(DiscordWriteGate gate, IDiscordRosterRepository roster)`, meant for `[TypeFilter(typeof(DiscordIngestGateFilter))]`;
    - `public const string LeaseItemKey = "DiscordWriteGate"` and `public const int RetryAfterSeconds = 30`;
    - `public TimeSpan WaitTimeout { get; init; } = 10 s`, which unit tests shorten.
    - Decision: after taking the lease, the filter checks the exclusion **again**. An admin erasure with `exclude=true` holds the same gate, so a request that waited behind it cannot recreate an excluded guild.

- [ ] **Step 1: Write the failing test**

Create `src/Collector.Api.Tests/Discord/DiscordWriteGateTests.cs`:

```csharp
using Collector.Api.Services.Discord;
using FluentAssertions;
using Xunit;

namespace Collector.Api.Tests.Discord;

/// <summary>Spec § 9.2: one writer at a time on the discord_* tables, and a lease that always frees the gate.</summary>
public class DiscordWriteGateTests
{
    [Fact]
    public async Task OneHolderAtATime()
    {
        var gate = new DiscordWriteGate();

        using var first = await gate.TryEnterAsync(TimeSpan.Zero, CancellationToken.None);

        first.Should().NotBeNull();
        (await gate.TryEnterAsync(TimeSpan.Zero, CancellationToken.None)).Should().BeNull("the gate is held");
    }

    [Fact]
    public async Task TimeoutWhileHeld_ReturnsNull()
    {
        var gate = new DiscordWriteGate();
        using var held = await gate.EnterAsync(CancellationToken.None);

        var lease = await gate.TryEnterAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None);

        lease.Should().BeNull();
    }

    [Fact]
    public async Task ReleasingTheLease_LetsTheNextWriterIn()
    {
        var gate = new DiscordWriteGate();
        var first = await gate.EnterAsync(CancellationToken.None);
        var second = gate.EnterAsync(CancellationToken.None);
        second.IsCompleted.Should().BeFalse("the first lease is still held");

        first.Dispose();

        using var lease = await second;
        lease.Should().NotBeNull();
    }

    [Fact]
    public async Task DisposingALeaseTwice_ReleasesOnce()
    {
        var gate = new DiscordWriteGate();
        var lease = await gate.EnterAsync(CancellationToken.None);

        lease.Dispose();
        lease.Dispose();

        using var again = await gate.TryEnterAsync(TimeSpan.Zero, CancellationToken.None);
        again.Should().NotBeNull();
        (await gate.TryEnterAsync(TimeSpan.Zero, CancellationToken.None))
            .Should().BeNull("a double dispose must not let two writers in");
    }

    [Fact]
    public async Task WaitingForTheGate_CanBeCancelled()
    {
        var gate = new DiscordWriteGate();
        using var held = await gate.EnterAsync(CancellationToken.None);
        using var cts = new CancellationTokenSource();
        var waiting = gate.EnterAsync(cts.Token);

        cts.Cancel();

        Func<Task> act = () => waiting;
        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test src/Collector.Api.Tests --filter "FullyQualifiedName~DiscordWriteGateTests"`

Expected: the build fails with `error CS0246: The type or namespace name 'DiscordWriteGate' could not be found`.

- [ ] **Step 3: Write the minimal implementation**

Create `src/Collector.Api/Services/Discord/DiscordWriteGate.cs`:

```csharp
namespace Collector.Api.Services.Discord;

/// <summary>
/// Serialises every write to the discord_* tables of tracker.db (spec § 9.2): ingestion, links
/// and rejections, erasures, and each retention batch. discord_accounts is shared by every
/// guild, so two concurrent syncs would otherwise insert the same account twice. The API runs
/// as a single instance and receives a few syncs an hour, so one process-wide semaphore costs
/// nothing. SQLite remains the only arbiter against the collector.
/// </summary>
public sealed class DiscordWriteGate
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    /// <summary>Waits at most <paramref name="timeout"/>; null when the gate stayed held.</summary>
    public async Task<IDisposable?> TryEnterAsync(TimeSpan timeout, CancellationToken ct) =>
        await _semaphore.WaitAsync(timeout, ct) ? new Lease(_semaphore) : null;

    /// <summary>Waits as long as needed, for background work that must not give up.</summary>
    public async Task<IDisposable> EnterAsync(CancellationToken ct)
    {
        await _semaphore.WaitAsync(ct);
        return new Lease(_semaphore);
    }

    /// <summary>Releases the gate once, however many times it is disposed.</summary>
    private sealed class Lease(SemaphoreSlim semaphore) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) semaphore.Release();
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test src/Collector.Api.Tests --filter "FullyQualifiedName~DiscordWriteGateTests"`

Expected: `Passed!  - Failed:     0, Passed:     5, Skipped:     0, Total:     5`

- [ ] **Step 5: Write the failing test for the filter**

Create `src/Collector.Api.Tests/Discord/DiscordIngestGateFilterTests.cs`:

```csharp
using Collector.Api.Errors;
using Collector.Api.Services.Discord;
using Collector.Data.Repositories;
using Collector.Discord;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Collector.Api.Tests.Discord;

/// <summary>
/// The resource filter in front of the ingest action (spec § 9.1 step 1), driven with a
/// hand-built MVC context. It checks what is refused before the gate is taken, and that the
/// gate is always released after the action.
/// </summary>
public class DiscordIngestGateFilterTests
{
    private const string GuildId = "123456789012345678";

    private static (ResourceExecutingContext Executing, ActionContext Action) ContextFor(string guildId)
    {
        var routeData = new RouteData();
        routeData.Values["guildId"] = guildId;
        var action = new ActionContext(new DefaultHttpContext(), routeData, new ActionDescriptor());
        return (new ResourceExecutingContext(action, new List<IFilterMetadata>(), new List<IValueProviderFactory>()), action);
    }

    private static async Task<bool> IsFreeAsync(DiscordWriteGate gate)
    {
        using var lease = await gate.TryEnterAsync(TimeSpan.Zero, CancellationToken.None);
        return lease is not null;
    }

    [Fact]
    public async Task InvalidGuildId_Is400InvalidSync_AndNothingElseRuns()
    {
        var gate = new DiscordWriteGate();
        var roster = new StubRoster(false);
        var filter = new DiscordIngestGateFilter(gate, roster);
        var (context, _) = ContextFor("not-a-snowflake");
        var actionRan = false;

        var act = () => filter.OnResourceExecutionAsync(context, () =>
        {
            actionRan = true;
            return Task.FromResult<ResourceExecutedContext>(null!);
        });

        (await act.Should().ThrowAsync<ValidationException>()).Which.Code.Should().Be("invalid_sync");
        actionRan.Should().BeFalse();
        roster.ExclusionChecks.Should().Be(0);
        (await IsFreeAsync(gate)).Should().BeTrue("the gate was never taken");
    }

    [Fact]
    public async Task ExcludedGuild_Is409GuildExcluded_WithoutTakingTheGate()
    {
        var gate = new DiscordWriteGate();
        var filter = new DiscordIngestGateFilter(gate, new StubRoster(true));
        var (context, _) = ContextFor(GuildId);
        var actionRan = false;

        var act = () => filter.OnResourceExecutionAsync(context, () =>
        {
            actionRan = true;
            return Task.FromResult<ResourceExecutedContext>(null!);
        });

        var thrown = (await act.Should().ThrowAsync<ConflictException>()).Which;
        thrown.Code.Should().Be("guild_excluded");
        thrown.StatusCode.Should().Be(409);
        actionRan.Should().BeFalse();
        (await IsFreeAsync(gate)).Should().BeTrue();
    }

    [Fact]
    public async Task GateStillHeldAfterTheWait_Is503Busy()
    {
        var gate = new DiscordWriteGate();
        using var holder = await gate.EnterAsync(CancellationToken.None);
        var roster = new StubRoster(false);
        var filter = new DiscordIngestGateFilter(gate, roster) { WaitTimeout = TimeSpan.Zero };
        var (context, _) = ContextFor(GuildId);
        var actionRan = false;

        var act = () => filter.OnResourceExecutionAsync(context, () =>
        {
            actionRan = true;
            return Task.FromResult<ResourceExecutedContext>(null!);
        });

        var thrown = (await act.Should().ThrowAsync<ServiceUnavailableException>()).Which;
        thrown.StatusCode.Should().Be(503);
        thrown.Code.Should().Be("busy");
        actionRan.Should().BeFalse();
        roster.ExclusionChecks.Should().Be(1);
    }

    [Fact]
    public async Task HappyPath_HoldsTheGateDuringTheAction_AndReleasesItAfter()
    {
        var gate = new DiscordWriteGate();
        var filter = new DiscordIngestGateFilter(gate, new StubRoster(false));
        var (context, action) = ContextFor(GuildId);
        object? leaseSeen = null;
        var gateFreeDuringAction = true;

        await filter.OnResourceExecutionAsync(context, async () =>
        {
            context.HttpContext.Items.TryGetValue(DiscordIngestGateFilter.LeaseItemKey, out leaseSeen);
            gateFreeDuringAction = await IsFreeAsync(gate);
            return new ResourceExecutedContext(action, new List<IFilterMetadata>());
        });

        leaseSeen.Should().BeAssignableTo<IDisposable>();
        gateFreeDuringAction.Should().BeFalse("the gate is held while the action runs");
        context.HttpContext.Items.ContainsKey(DiscordIngestGateFilter.LeaseItemKey).Should().BeFalse();
        (await IsFreeAsync(gate)).Should().BeTrue("the lease was released after the action");
    }

    [Fact]
    public async Task ActionThatThrows_StillReleasesTheGate()
    {
        var gate = new DiscordWriteGate();
        var filter = new DiscordIngestGateFilter(gate, new StubRoster(false));
        var (context, _) = ContextFor(GuildId);

        var act = () => filter.OnResourceExecutionAsync(context, () => throw new InvalidOperationException("boom"));

        await act.Should().ThrowAsync<InvalidOperationException>();
        (await IsFreeAsync(gate)).Should().BeTrue();
    }

    [Fact]
    public async Task GuildExcludedWhileWaitingForTheGate_Is409_AndReleasesTheGate()
    {
        var gate = new DiscordWriteGate();
        var roster = new StubRoster(false, true);
        var filter = new DiscordIngestGateFilter(gate, roster);
        var (context, _) = ContextFor(GuildId);
        var actionRan = false;

        var act = () => filter.OnResourceExecutionAsync(context, () =>
        {
            actionRan = true;
            return Task.FromResult<ResourceExecutedContext>(null!);
        });

        (await act.Should().ThrowAsync<ConflictException>()).Which.Code.Should().Be("guild_excluded");
        actionRan.Should().BeFalse();
        roster.ExclusionChecks.Should().Be(2, "the exclusion is checked again once the gate is held");
        (await IsFreeAsync(gate)).Should().BeTrue();
    }

    /// <summary>Answers the exclusion checks in order (the last answer repeats); nothing else is used.</summary>
    private sealed class StubRoster(params bool[] excludedAnswers) : IDiscordRosterRepository
    {
        public int ExclusionChecks { get; private set; }

        public Task<bool> IsGuildExcludedAsync(string guildId, CancellationToken ct = default)
        {
            var answer = excludedAnswers[Math.Min(ExclusionChecks, excludedAnswers.Length - 1)];
            ExclusionChecks++;
            return Task.FromResult(answer);
        }

        public Task<DateTime?> GetLastSyncReceivedAtAsync(string guildId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<RosterSnapshot> LoadSnapshotAsync(
            string guildId, IReadOnlyCollection<string> payloadUserIds, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlySet<string>> GetOptedOutAsync(IReadOnlyCollection<string> userIds, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<DiscordSyncResult> ApplyAsync(DiscordSyncWrite write, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}

/// <summary>The gate is one instance for the whole API, and MVC can build the filter from DI.</summary>
[Collection(ApiCollection.Name)]
public class DiscordIngestGateRegistrationTests(ApiFactory factory)
{
    [Fact]
    public void TheApi_SharesOneWriteGate_AndCanBuildTheIngestFilter()
    {
        var gate = factory.Services.GetRequiredService<DiscordWriteGate>();
        factory.Services.GetRequiredService<DiscordWriteGate>().Should().BeSameAs(gate);

        using var scope = factory.Services.CreateScope();
        var filter = ActivatorUtilities.CreateInstance<DiscordIngestGateFilter>(scope.ServiceProvider);

        filter.WaitTimeout.Should().Be(TimeSpan.FromSeconds(10));
    }
}
```

- [ ] **Step 6: Run it to verify it fails**

Run: `dotnet test src/Collector.Api.Tests --filter "FullyQualifiedName~DiscordIngestGate"`

Expected: the build fails with `error CS0246: The type or namespace name 'DiscordIngestGateFilter' could not be found`.

- [ ] **Step 7: Write the minimal implementation**

Create `src/Collector.Api/Services/Discord/DiscordIngestGateFilter.cs`:

```csharp
using Collector.Api.Errors;
using Collector.Data.Repositories;
using Collector.Discord;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Collector.Api.Services.Discord;

/// <summary>
/// Resource filter of the ingest action (spec § 9.1 step 1). It runs after authentication,
/// the rate limiter and authorization, and before the body is bound, so an invalid or excluded
/// guild is refused without reading up to 25 MiB. It holds the Discord write gate from model
/// binding to the end of the response, and releases it whatever happens.
/// </summary>
public sealed class DiscordIngestGateFilter : IAsyncResourceFilter
{
    /// <summary>Key of the lease in <c>HttpContext.Items</c> while the action runs.</summary>
    public const string LeaseItemKey = "DiscordWriteGate";

    /// <summary>Seconds a refused writer is told to wait (<c>Retry-After</c>).</summary>
    public const int RetryAfterSeconds = 30;

    private readonly DiscordWriteGate _gate;
    private readonly IDiscordRosterRepository _roster;

    public DiscordIngestGateFilter(DiscordWriteGate gate, IDiscordRosterRepository roster)
    {
        _gate = gate;
        _roster = roster;
    }

    /// <summary>How long a request waits for the gate before a 503. Unit tests shorten it.</summary>
    public TimeSpan WaitTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        var guildId = context.RouteData.Values["guildId"] as string;
        if (!DiscordSnowflake.IsValid(guildId))
            throw new ValidationException("guildId (URL) : snowflake attendu.", DiscordErrorCodes.InvalidSync);

        var ct = context.HttpContext.RequestAborted;
        if (await _roster.IsGuildExcludedAsync(guildId!, ct)) throw GuildExcluded();

        var lease = await _gate.TryEnterAsync(WaitTimeout, ct)
            ?? throw new ServiceUnavailableException(
                "Tracker occupé, réessaie dans 30 s.", RetryAfterSeconds, DiscordErrorCodes.Busy);
        try
        {
            // An admin may have excluded the guild while this request waited. Erasures take
            // the same gate, so this second check cannot be overtaken.
            if (await _roster.IsGuildExcludedAsync(guildId!, ct)) throw GuildExcluded();

            context.HttpContext.Items[LeaseItemKey] = lease;
            await next();
        }
        finally
        {
            context.HttpContext.Items.Remove(LeaseItemKey);
            lease.Dispose();
        }
    }

    private static ConflictException GuildExcluded() =>
        new("Ce serveur est exclu du suivi.", DiscordErrorCodes.GuildExcluded);
}
```

In `src/Collector.Api/Extensions/ServiceCollectionExtensions.cs`, method `AddApiServices`, replace:

```csharp
        services.AddScoped<StatsService>();
```

with:

```csharp
        services.AddScoped<StatsService>();

        // Discord rosters: one process-wide gate serialises every write to the discord_*
        // tables (ingestion, links, erasures, retention batches).
        services.AddSingleton<Collector.Api.Services.Discord.DiscordWriteGate>();
```

- [ ] **Step 8: Run the tests to verify they pass**

Run: `dotnet test src/Collector.Api.Tests --filter "FullyQualifiedName~DiscordIngestGate"`

Expected: `Passed!  - Failed:     0, Passed:     7, Skipped:     0, Total:     7`

Run: `dotnet test src/Collector.Api.Tests --filter "FullyQualifiedName~DiscordWriteGateTests"`

Expected: `Passed!  - Failed:     0, Passed:     5, Skipped:     0, Total:     5`

- [ ] **Step 9: Commit**

```bash
git add src/Collector.Api/Services/Discord/DiscordWriteGate.cs \
  src/Collector.Api/Services/Discord/DiscordIngestGateFilter.cs \
  src/Collector.Api/Extensions/ServiceCollectionExtensions.cs \
  src/Collector.Api.Tests/Discord/DiscordWriteGateTests.cs \
  src/Collector.Api.Tests/Discord/DiscordIngestGateFilterTests.cs
git commit -m "$(cat <<'EOF'
feat(api): one gate serialises Discord writes and guards the ingest route

discord_accounts is shared by every guild: two concurrent syncs would insert the
same account twice, and an erasure could interleave with an ingestion. A single
process-wide gate orders them. The ingest resource filter refuses a bad or
excluded guild before the body is read, answers 503 with Retry-After when the
gate stays held for 10 s, and checks the exclusion again under the gate, so an
admin exclusion is never overtaken by a request that was waiting.

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
EOF
)"
```

### Task A11: DiscordIngestService, DiscordIngestController and ingest integration tests

**Files:**
- Create: `src/Collector.Api/Services/Discord/DiscordIngestService.cs`
- Create: `src/Collector.Api/Controllers/DiscordIngestController.cs`
- Modify: `src/Collector.Api/Extensions/ServiceCollectionExtensions.cs` (method `AddApiServices`, the `DiscordWriteGate` line added by Task A10)
- Create: `src/Collector.Api.Tests/Discord/DiscordTestKit.cs`
- Test: `src/Collector.Api.Tests/Discord/DiscordIngestTests.cs`

**Interfaces:**
- Consumes:
  - Task A9: `DiscordSyncValidator.Normalize`, `DiscordSyncValidator.MaxMembers`, the ingest DTOs and `DiscordErrorCodes`.
  - Task A10: `DiscordWriteGate` (singleton) and `DiscordIngestGateFilter`.
  - CONTRACTS § 3:
    - `IDiscordRosterRepository.GetLastSyncReceivedAtAsync`, `LoadSnapshotAsync`, `GetOptedOutAsync` and `ApplyAsync`;
    - `DiscordSyncWrite(Sync, Plan, CollectedAt, ReceivedAt, SubmittedByApiUserId, SubmittedByUsername)`;
    - `DiscordSyncResult(SyncId, OrgSid)`.
  - CONTRACTS § 2: `DiscordRosterDiff.Compute(guildId, sync, collectedAt, snapshot, optedOut)`, `RosterPlan`, `PlannedEvent`, `DiscordStoreBusyException` and `DiscordFormats.Iso`.
  - CONTRACTS § 1: `DiscordEventTypes`, the `TrackerDbContext` DbSets (`DiscordGuilds`, `DiscordRoles`, `DiscordAccounts`, `DiscordMembers`, `DiscordMemberEvents`, `DiscordSyncs`, `DiscordOptOuts`, `DiscordGuildOptOuts`) and their entities.
  - CONTRACTS § 4:
    - `DiscordIngestAuth.PolicyName` and `RateLimitingExtensions.DiscordIngestPolicy`;
    - `RateLimitSettings.DiscordIngest.PermitLimit`;
    - `ConflictException(string, string?)` and `ServiceUnavailableException(string, int, string?)`;
    - `ExceptionHandlingMiddleware` writing `code` and `Retry-After`, and mapping `BadHttpRequestException` to 413 `application/problem+json`;
    - `POST /api/api-keys { name, expiresAt, scope }`.
  - Existing code: `CurrentUserAccessor.UserId` and `Username`, `ApiFactory`, `PUT /api/admin/users/{id} { isBanned }` and `GET /api/auth/me`.
- Produces:
  - `DiscordIngestService.IngestAsync(string guildId, DiscordSyncRequest request, DateTime receivedAt, long submitterId, string submitterName, CancellationToken ct) : Task<DiscordSyncResponseDto>`, registered as scoped, with `public const int BusyRetryAfterSeconds = 30`.
  - `DiscordIngestController` with `public const long MaxBodyBytes = 25L * 1024 * 1024`, at route `POST api/ingest/discord/guilds/{guildId}/syncs`.
  - `TimeProvider.System`, registered as a singleton in `AddApiServices`. Tests replace it with `ConfigureTestServices`.
  - `DiscordTestKit`, exactly as CONTRACTS § 5. Its builders use these defaults:
    - guild name `"Guild {id}"`, no icon, `memberCount` = number of members;
    - `pluginVersion` `"1.0.0"`, `collectedAt` = now;
    - `Role` color `"#e67e22"`.
    - `durationMs` keeps the CONTRACTS default of 1000. A sync is dated `ReceivedAt − durationMs`, so a second sync of the same guild received less than 1 s after the previous one is a 409 `stale_sync`. A test that posts several syncs of one guild in a row passes `durationMs: 0` for each sync after the first, or moves a test clock, as the tests below do.
  - Decisions:
    - `ReceivedAt` is read in the action, under the gate and after binding, so the accepted syncs of a guild get increasing `ReceivedAt` values in commit order.
    - A sync is stale when the last `ReceivedAt` is **strictly** later than its `CollectedAt`.
    - `membersReceived` is the raw member count, opt-outs included.
    - `nameChanged` = `username_changed` + `global_name_changed`.
    - The false-departure deletions are not counted in `events`.

- [ ] **Step 1: Write the failing test**

Create `src/Collector.Api.Tests/Discord/DiscordTestKit.cs`:

```csharp
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Collector.Api.Dtos.Discord;

namespace Collector.Api.Tests.Discord;

/// <summary>
/// Builders shared by the Discord API tests: a client carrying only a discord:ingest key,
/// unique snowflakes, and sync bodies with sensible defaults.
/// </summary>
public static class DiscordTestKit
{
    private static long _lastSnowflake = 100_000_000_000_000_000;

    /// <summary>Creates a user, signs in, creates a discord:ingest key (180 days) and returns a client carrying only x-api-key.</summary>
    public static async Task<(HttpClient Client, string RawKey, long UserId)> IngestClientAsync(ApiFactory f, string username)
    {
        using var owner = await f.SignedInClientAsync(username);
        var me = await owner.GetFromJsonAsync<JsonElement>("/api/auth/me");
        var created = await owner.PostAsJsonAsync("/api/api-keys", new
        {
            name = $"Vencord {username}",
            expiresAt = DateTime.UtcNow.AddDays(180),
            scope = "discord:ingest",
        });
        created.EnsureSuccessStatusCode();
        var rawKey = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("rawKey").GetString()!;

        var client = f.CreateClient();
        client.DefaultRequestHeaders.Add("x-api-key", rawKey);
        return (client, rawKey, me.GetProperty("id").GetInt64());
    }

    /// <summary>Unique 18-digit string per call (thread-safe counter).</summary>
    public static string NewSnowflake() =>
        Interlocked.Increment(ref _lastSnowflake).ToString(CultureInfo.InvariantCulture);

    /// <summary>A sync of <paramref name="guildId"/>; <paramref name="expectedCount"/> defaults to the number of members.</summary>
    public static DiscordSyncRequest Sync(string guildId, IEnumerable<DiscordSyncMember> members,
        IEnumerable<DiscordSyncRole>? roles = null, bool complete = true, string method = "member-search",
        long durationMs = 1000, int? expectedCount = null)
    {
        var memberList = members.ToList();
        return new DiscordSyncRequest(
            PluginVersion: "1.0.0",
            CollectedAt: DateTimeOffset.UtcNow,
            CollectionDurationMs: durationMs,
            Guild: new DiscordSyncGuild(guildId, $"Guild {guildId}", null, memberList.Count),
            Coverage: new DiscordSyncCoverage(method, complete, expectedCount ?? memberList.Count, memberList.Count),
            Roles: roles?.ToList() ?? [],
            Members: memberList);
    }

    public static DiscordSyncMember Member(string userId, string username, IEnumerable<string>? roleIds = null,
        string? nick = null, string? globalName = null, DateTimeOffset? joinedAt = null, bool bot = false) =>
        new(userId, username, globalName, nick, roleIds?.ToList() ?? [], joinedAt, bot);

    public static DiscordSyncRole Role(string roleId, string name, int position, bool hoist = true, bool managed = false) =>
        new(roleId, name, position, "#e67e22", hoist, managed);

    public static Task<HttpResponseMessage> PostSyncAsync(HttpClient ingestClient, string guildId, DiscordSyncRequest body) =>
        ingestClient.PostAsJsonAsync($"/api/ingest/discord/guilds/{guildId}/syncs", body);
}
```

Create `src/Collector.Api.Tests/Discord/DiscordIngestTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Collector.Api.Controllers;
using Collector.Api.Data;
using Collector.Api.Dtos.Discord;
using Collector.Api.Options;
using Collector.Api.Services.Discord;
using Collector.Data;
using Collector.Discord;
using Collector.Models;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Collector.Api.Tests.Discord.DiscordTestKit;

namespace Collector.Api.Tests.Discord;

/// <summary>
/// <c>POST api/ingest/discord/guilds/{guildId}/syncs</c> end to end (spec § 9 and § 15
/// "Ingestion"). It covers who may post, what each kind of sync records, and how the route
/// behaves under contention and with oversized or malformed bodies.
/// </summary>
[Collection(ApiCollection.Name)]
public class DiscordIngestTests(ApiFactory factory)
{
    /// <summary>
    /// Baselines are sent with the longest allowed collection, so they are dated 30 min in the
    /// past. A member who joined 10 min ago in a later sync therefore joined after tracking began.
    /// </summary>
    private const long BaselineDurationMs = 1_800_000;

    private static readonly DateTimeOffset LongAgo = DateTimeOffset.UtcNow.AddDays(-400);

    // --- access ---

    [Fact]
    public async Task ScopedKey_IsAccepted()
    {
        var (client, _, _) = await IngestClientAsync(factory, "ding-scoped");
        var guild = NewSnowflake();

        var result = await PostOkAsync(client, guild, Sync(guild, [Member(NewSnowflake(), "scoped", joinedAt: LongAgo)], durationMs: 0));

        result.GetProperty("syncId").GetInt64().Should().BePositive();
        result.GetProperty("isBaseline").GetBoolean().Should().BeTrue();
        result.GetProperty("orgSid").ValueKind.Should().Be(JsonValueKind.Null, "the guild is tied to no org yet");
    }

    [Theory]
    [InlineData("none")]
    [InlineData("jwt")]
    [InlineData("full-key")]
    [InlineData("admin-static-key")]
    [InlineData("revoked-key")]
    [InlineData("expired-key")]
    [InlineData("banned-owner")]
    public async Task EveryOtherCredential_Is401(string credential)
    {
        var client = await ClientWithAsync(credential, $"ding-401-{credential}");
        var guild = NewSnowflake();

        using var response = await PostSyncAsync(client, guild, Sync(guild, [Member(NewSnowflake(), "refused", joinedAt: LongAgo)], durationMs: 0));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await DbAsync(db => db.DiscordGuilds.AnyAsync(g => g.GuildId == guild))).Should().BeFalse();
    }

    // --- what a sync records ---

    [Fact]
    public async Task Baseline_RecordsNoGuildEvent_ButReportsTheRenameOfAKnownAccount()
    {
        var (client, _, _) = await IngestClientAsync(factory, "ding-baseline");
        var (first, second) = (NewSnowflake(), NewSnowflake());
        var (pilot, other, recent) = (NewSnowflake(), NewSnowflake(), NewSnowflake());

        // "recent" joined while this 30-minute collection ran: a baseline still records no arrival.
        var baseline = await PostOkAsync(client, first, Sync(first,
        [
            Member(pilot, "pilote42", joinedAt: LongAgo),
            Member(other, "other", joinedAt: LongAgo),
            Member(recent, "recent", joinedAt: DateTimeOffset.UtcNow.AddMinutes(-5)),
        ], durationMs: BaselineDurationMs));
        var renamed = await PostOkAsync(client, second, Sync(second,
            [Member(pilot, "pilote43", joinedAt: LongAgo)], durationMs: 0));

        baseline.GetProperty("isBaseline").GetBoolean().Should().BeTrue();
        ShouldCount(baseline);
        renamed.GetProperty("isBaseline").GetBoolean().Should().BeTrue();
        ShouldCount(renamed, nameChanged: 1);
        (await GuildEventsAsync(first)).Should().BeEmpty();
        (await GuildEventsAsync(second)).Should().BeEmpty();
        var rename = (await AccountEventsAsync(pilot)).Should().ContainSingle().Which;
        rename.Type.Should().Be(DiscordEventTypes.UsernameChanged);
        rename.OldValue.Should().Be("pilote42");
        rename.NewValue.Should().Be("pilote43");
        (await DbAsync(db => db.DiscordMembers.CountAsync(m => m.GuildId == first))).Should().Be(3);
    }

    [Fact]
    public async Task CompleteSync_RecordsJoinsDeparturesAndRoleNickAndNameChanges()
    {
        var (client, _, _) = await IngestClientAsync(factory, "ding-diff");
        var guild = NewSnowflake();
        var (officer, pilot) = (NewSnowflake(), NewSnowflake());
        var roles = new[] { Role(officer, "Officier", 12), Role(pilot, "Pilote", 5) };
        var (promoted, renick, rename, leaver, stayer, newcomer) =
            (NewSnowflake(), NewSnowflake(), NewSnowflake(), NewSnowflake(), NewSnowflake(), NewSnowflake());
        await PostOkAsync(client, guild, Sync(guild,
        [
            Member(promoted, "promoted", [pilot], joinedAt: LongAgo),
            Member(renick, "renick", nick: "Old", joinedAt: LongAgo),
            Member(rename, "rename", globalName: "Old Name", joinedAt: LongAgo),
            Member(leaver, "leaver", joinedAt: LongAgo),
            Member(stayer, "stayer", joinedAt: LongAgo),
        ], roles, durationMs: BaselineDurationMs));
        var joinedAt = DateTimeOffset.UtcNow.AddMinutes(-10);

        var result = await PostOkAsync(client, guild, Sync(guild,
        [
            Member(promoted, "promoted", [pilot, officer], joinedAt: LongAgo),
            Member(renick, "renick", nick: "New", joinedAt: LongAgo),
            Member(rename, "rename", globalName: "New Name", joinedAt: LongAgo),
            Member(stayer, "stayer", joinedAt: LongAgo),
            Member(newcomer, "newcomer", joinedAt: joinedAt),
        ], roles, durationMs: 0));

        result.GetProperty("isBaseline").GetBoolean().Should().BeFalse();
        result.GetProperty("isComplete").GetBoolean().Should().BeTrue();
        ShouldCount(result, joined: 1, left: 1, rolesChanged: 1, nickChanged: 1, nameChanged: 1);
        var events = await GuildEventsAsync(guild);
        var joined = events.Should().ContainSingle(e => e.Type == DiscordEventTypes.Joined).Which;
        joined.DiscordUserId.Should().Be(newcomer);
        joined.OccurredAt.Should().Be(Second(joinedAt), "an arrival is dated by Discord's JoinedAt");
        joined.NewValue.Should().Be(DiscordFormats.Iso(Second(joinedAt)));
        events.Should().ContainSingle(e => e.Type == DiscordEventTypes.Left).Which.DiscordUserId.Should().Be(leaver);
        var rolesChanged = events.Should().ContainSingle(e => e.Type == DiscordEventTypes.RolesChanged).Which;
        rolesChanged.DiscordUserId.Should().Be(promoted);
        rolesChanged.OldValue.Should().Contain("Pilote").And.NotContain("Officier");
        rolesChanged.NewValue.Should().Contain("Pilote").And.Contain("Officier");
        var nick = events.Should().ContainSingle(e => e.Type == DiscordEventTypes.NickChanged).Which;
        nick.OldValue.Should().Be("Old");
        nick.NewValue.Should().Be("New");
        var globalName = (await AccountEventsAsync(rename)).Should().ContainSingle().Which;
        globalName.Type.Should().Be(DiscordEventTypes.GlobalNameChanged);
        globalName.NewValue.Should().Be("New Name");
        (await DbAsync(db => db.DiscordMembers.SingleAsync(m => m.GuildId == guild && m.DiscordUserId == leaver)))
            .LeftAt.Should().NotBeNull();
    }

    [Fact]
    public async Task DeletedRole_IsNotABurstOfRoleChanges_AndIsCounted()
    {
        var (client, _, _) = await IngestClientAsync(factory, "ding-deleted-role");
        var guild = NewSnowflake();
        var (kept, deleted) = (NewSnowflake(), NewSnowflake());
        var members = new[]
        {
            Member(NewSnowflake(), "both", [kept, deleted], joinedAt: LongAgo),
            Member(NewSnowflake(), "only-deleted-a", [deleted], joinedAt: LongAgo),
            Member(NewSnowflake(), "only-deleted-b", [deleted], joinedAt: LongAgo),
        };
        await PostOkAsync(client, guild, Sync(guild, members, [Role(kept, "Officier", 2), Role(deleted, "Recrue", 1)], durationMs: 0));

        // The role was deleted during the collection: the members still reference it.
        var result = await PostOkAsync(client, guild, Sync(guild, members, [Role(kept, "Officier", 2)], durationMs: 0));

        ShouldCount(result);
        result.GetProperty("unknownRoleRefs").GetInt32().Should().Be(3);
        (await DbAsync(db => db.DiscordRoles.SingleAsync(r => r.GuildId == guild && r.RoleId == deleted)))
            .DeletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task ReturningMember_IsARejoin_UnlessTheSameStayProvesTheDepartureWasFalse()
    {
        var (client, _, _) = await IngestClientAsync(factory, "ding-rejoin");
        var guild = NewSnowflake();
        var (returning, missed) = (NewSnowflake(), NewSnowflake());
        var missedJoin = DateTimeOffset.UtcNow.AddDays(-300);
        var stayers = Enumerable.Range(0, 3).Select(i => Member(NewSnowflake(), $"stayer{i}", joinedAt: LongAgo)).ToList();
        await PostOkAsync(client, guild, Sync(guild,
            [.. stayers, Member(returning, "returning", joinedAt: LongAgo), Member(missed, "missed", joinedAt: missedJoin)],
            durationMs: BaselineDurationMs));
        ShouldCount(await PostOkAsync(client, guild, Sync(guild, stayers, durationMs: 0)), left: 2);
        var rejoinedAt = DateTimeOffset.UtcNow.AddMinutes(-5);

        var result = await PostOkAsync(client, guild, Sync(guild,
            [.. stayers, Member(returning, "returning", joinedAt: rejoinedAt), Member(missed, "missed", joinedAt: missedJoin)],
            durationMs: 0));

        ShouldCount(result, rejoined: 1);
        var events = await GuildEventsAsync(guild);
        events.Where(e => e.DiscordUserId == missed).Should().BeEmpty("the same JoinedAt proves the departure was false");
        var returned = events.Where(e => e.DiscordUserId == returning).ToList();
        returned.Select(e => e.Type).Should().Equal(DiscordEventTypes.Left, DiscordEventTypes.Rejoined);
        returned[1].OccurredAt.Should().Be(Second(rejoinedAt));
        returned[1].OldValue.Should().Be(DiscordFormats.Iso(Second(LongAgo)));
        returned[1].NewValue.Should().Be(DiscordFormats.Iso(Second(rejoinedAt)));
        (await DbAsync(db => db.DiscordMembers.CountAsync(m => m.GuildId == guild && m.LeftAt != null))).Should().Be(0);
    }

    [Fact]
    public async Task ActiveMember_WithALaterJoinedAt_LeftAndCameBack_AnEarlierOneIsIgnored()
    {
        var (client, _, _) = await IngestClientAsync(factory, "ding-later-join");
        var guild = NewSnowflake();
        var (member, other) = (NewSnowflake(), NewSnowflake());
        await PostOkAsync(client, guild, Sync(guild,
            [Member(member, "member", joinedAt: LongAgo), Member(other, "other", joinedAt: LongAgo)], durationMs: 0));
        var later = DateTimeOffset.UtcNow.AddMinutes(-5);

        var rejoined = await PostOkAsync(client, guild, Sync(guild,
            [Member(member, "member", joinedAt: later), Member(other, "other", joinedAt: LongAgo)], durationMs: 0));
        var earlier = await PostOkAsync(client, guild, Sync(guild,
            [Member(member, "member", joinedAt: LongAgo), Member(other, "other", joinedAt: LongAgo)], durationMs: 0));

        ShouldCount(rejoined, rejoined: 1);
        (await GuildEventsAsync(guild)).Should().ContainSingle().Which.OccurredAt.Should().Be(Second(later));
        ShouldCount(earlier);
        (await DbAsync(db => db.DiscordMembers.SingleAsync(m => m.GuildId == guild && m.DiscordUserId == member)))
            .JoinedAt.Should().Be(Second(later));
    }

    [Fact]
    public async Task JoinedAt_SentWithAnotherOffset_IsTheSameInstant()
    {
        var (client, _, _) = await IngestClientAsync(factory, "ding-offset");
        var guild = NewSnowflake();
        var member = NewSnowflake();
        var utc = new DateTimeOffset(2025, 3, 14, 20, 11, 5, 123, TimeSpan.Zero);
        await PostOkAsync(client, guild, Sync(guild, [Member(member, "offset", joinedAt: utc)], durationMs: 0));

        var result = await PostOkAsync(client, guild, Sync(guild,
            [Member(member, "offset", joinedAt: utc.ToOffset(TimeSpan.FromHours(2)))], durationMs: 0));

        ShouldCount(result);
        (await DbAsync(db => db.DiscordMembers.SingleAsync(m => m.GuildId == guild && m.DiscordUserId == member)))
            .JoinedAt.Should().Be(new DateTime(2025, 3, 14, 20, 11, 5, DateTimeKind.Utc));
    }

    [Theory]
    [InlineData(false, "member-search")]
    [InlineData(true, "role-members")]
    [InlineData(true, "cache")]
    public async Task SyncsThatCannotBeComplete_NeverRecordDepartures(bool declaredComplete, string method)
    {
        var (client, _, _) = await IngestClientAsync(factory, $"ding-partial-{method}-{declaredComplete}");
        var guild = NewSnowflake();
        var members = Enumerable.Range(0, 3).Select(i => Member(NewSnowflake(), $"partial{i}", joinedAt: LongAgo)).ToList();
        await PostOkAsync(client, guild, Sync(guild, members, durationMs: 0));

        var result = await PostOkAsync(client, guild, Sync(guild, members.Take(1), complete: declaredComplete, method: method, durationMs: 0));

        result.GetProperty("isComplete").GetBoolean().Should().BeFalse();
        ShouldCount(result);
        (await DbAsync(db => db.DiscordMembers.CountAsync(m => m.GuildId == guild && m.LeftAt == null))).Should().Be(3);
    }

    [Fact]
    public async Task DeclaredComplete_WithFewerMembersThanExpected_IsPartial()
    {
        var (client, _, _) = await IngestClientAsync(factory, "ding-mismatch");
        var guild = NewSnowflake();
        var members = Enumerable.Range(0, 3).Select(i => Member(NewSnowflake(), $"mismatch{i}", joinedAt: LongAgo)).ToList();
        await PostOkAsync(client, guild, Sync(guild, members, durationMs: 0));

        var result = await PostOkAsync(client, guild, Sync(guild, members.Take(1), expectedCount: 3, durationMs: 0));

        result.GetProperty("isComplete").GetBoolean().Should().BeFalse("1 member collected out of 3 announced");
        ShouldCount(result);
        var syncs = await DbAsync(db => db.DiscordSyncs.AsNoTracking().Where(s => s.GuildId == guild).OrderBy(s => s.Id).ToListAsync());
        syncs[1].DeclaredComplete.Should().BeTrue();
        syncs[1].IsComplete.Should().BeFalse();
    }

    [Fact]
    public async Task IdenticalRepost_CreatesNoEvent()
    {
        var (client, _, _) = await IngestClientAsync(factory, "ding-identical");
        var guild = NewSnowflake();
        var role = NewSnowflake();
        var body = Sync(guild,
            [Member(NewSnowflake(), "same", [role], nick: "Same", globalName: "Same", joinedAt: LongAgo)],
            [Role(role, "Membre", 1)], durationMs: 0);
        await PostOkAsync(client, guild, body);

        var again = await PostOkAsync(client, guild, body);

        ShouldCount(again);
        (await GuildEventsAsync(guild)).Should().BeEmpty();
        (await DbAsync(db => db.DiscordSyncs.CountAsync(s => s.GuildId == guild))).Should().Be(2);
    }

    [Fact]
    public async Task MemberFromBeforeTracking_IsStoredWithoutAJoin()
    {
        var (client, _, _) = await IngestClientAsync(factory, "ding-pre-tracking");
        var guild = NewSnowflake();
        var (known, early, undated) = (NewSnowflake(), NewSnowflake(), NewSnowflake());
        await PostOkAsync(client, guild, Sync(guild, [Member(known, "known", joinedAt: LongAgo)],
            complete: false, method: "role-members", durationMs: 0));

        var result = await PostOkAsync(client, guild, Sync(guild,
        [
            Member(known, "known", joinedAt: LongAgo),
            Member(early, "early", joinedAt: LongAgo),   // joined before the baseline, missed by it
            Member(undated, "undated"),                  // no JoinedAt and no complete sync before
        ], durationMs: 0));

        ShouldCount(result);
        (await GuildEventsAsync(guild)).Should().BeEmpty();
        (await DbAsync(db => db.DiscordMembers.CountAsync(m => m.GuildId == guild && m.LeftAt == null))).Should().Be(3);
    }

    [Fact]
    public async Task OptedOutMember_IsNeverStored_AndTheSyncStaysComplete()
    {
        var (client, _, _) = await IngestClientAsync(factory, "ding-optout");
        var guild = NewSnowflake();
        var (kept, optedOut) = (NewSnowflake(), NewSnowflake());
        await DbAsync(db =>
        {
            db.DiscordOptOuts.Add(new DiscordOptOut { DiscordUserId = optedOut, CreatedAt = DateTime.UtcNow, ByUsername = "admin" });
            return db.SaveChangesAsync();
        });

        var result = await PostOkAsync(client, guild, Sync(guild,
            [Member(kept, "kept", joinedAt: LongAgo), Member(optedOut, "optedout", joinedAt: LongAgo)], durationMs: 0));

        result.GetProperty("isComplete").GetBoolean().Should().BeTrue("completeness is judged on the message as sent");
        result.GetProperty("membersReceived").GetInt32().Should().Be(2);
        result.GetProperty("membersOptedOut").GetInt32().Should().Be(1);
        (await DbAsync(db => db.DiscordMembers.AnyAsync(m => m.DiscordUserId == optedOut))).Should().BeFalse();
        (await DbAsync(db => db.DiscordAccounts.AnyAsync(a => a.DiscordUserId == optedOut))).Should().BeFalse();
        (await DbAsync(db => db.DiscordSyncs.SingleAsync(s => s.GuildId == guild))).OptedOutCount.Should().Be(1);
    }

    [Fact]
    public async Task MassDeparture_TripsTheGuard_UntilTheNextCompleteSyncIsAllowedThrough()
    {
        var (client, _, _) = await IngestClientAsync(factory, "ding-guard");
        var guild = NewSnowflake();
        var members = Enumerable.Range(0, 40).Select(i => Member(NewSnowflake(), $"guard{i}", joinedAt: LongAgo)).ToList();
        await PostOkAsync(client, guild, Sync(guild, members, durationMs: 0));
        var remaining = members.Take(28).ToList();   // 12 departures, 30 % of the guild

        var tripped = await PostOkAsync(client, guild, Sync(guild, remaining, durationMs: 0));

        tripped.GetProperty("departureGuardTripped").GetBoolean().Should().BeTrue();
        tripped.GetProperty("isComplete").GetBoolean().Should().BeFalse();
        ShouldCount(tripped);
        (await DbAsync(db => db.DiscordMembers.CountAsync(m => m.GuildId == guild && m.LeftAt != null))).Should().Be(0);

        // Seeded directly: this test is about how the ingestion uses a lift, not how an admin grants it.
        await DbAsync(db => db.DiscordGuilds.Where(g => g.GuildId == guild)
            .ExecuteUpdateAsync(s => s.SetProperty(g => g.AllowMassDepartureOnce, true)));

        var allowed = await PostOkAsync(client, guild, Sync(guild, remaining, durationMs: 0));

        allowed.GetProperty("departureGuardTripped").GetBoolean().Should().BeFalse();
        allowed.GetProperty("isComplete").GetBoolean().Should().BeTrue();
        ShouldCount(allowed, left: 12);
        (await DbAsync(db => db.DiscordGuilds.SingleAsync(g => g.GuildId == guild)))
            .AllowMassDepartureOnce.Should().BeFalse("the lift is used up");
        (await DbAsync(db => db.DiscordSyncs.Where(s => s.GuildId == guild).OrderBy(s => s.Id)
                .Select(s => s.DepartureGuardTripped).ToListAsync()))
            .Should().Equal(false, true, false);
    }

    // --- refusals ---

    [Fact]
    public async Task EmptyCompleteSync_Is400EmptyCompleteSync()
    {
        var (client, _, _) = await IngestClientAsync(factory, "ding-empty");
        var guild = NewSnowflake();

        using var response = await PostSyncAsync(client, guild, Sync(guild, [], durationMs: 0));

        (await ProblemAsync(response, HttpStatusCode.BadRequest)).GetProperty("code").GetString().Should().Be("empty_complete_sync");
        (await DbAsync(db => db.DiscordGuilds.AnyAsync(g => g.GuildId == guild))).Should().BeFalse();
    }

    [Fact]
    public async Task SyncCollectedBeforeTheLastAcceptedOne_Is409Stale()
    {
        var (client, _, _) = await IngestClientAsync(factory, "ding-stale");
        var guild = NewSnowflake();
        var members = new[] { Member(NewSnowflake(), "stale-a", joinedAt: LongAgo), Member(NewSnowflake(), "stale-b", joinedAt: LongAgo) };
        await PostOkAsync(client, guild, Sync(guild, members, durationMs: 0));

        // This collection started 10 min ago, before the baseline was received.
        using var response = await PostSyncAsync(client, guild, Sync(guild, members.Take(1), durationMs: 600_000));

        (await ProblemAsync(response, HttpStatusCode.Conflict)).GetProperty("code").GetString().Should().Be("stale_sync");
        (await DbAsync(db => db.DiscordSyncs.CountAsync(s => s.GuildId == guild))).Should().Be(1);
        (await DbAsync(db => db.DiscordMembers.CountAsync(m => m.GuildId == guild && m.LeftAt != null))).Should().Be(0);
    }

    [Fact]
    public async Task ExcludedGuild_Is409GuildExcluded()
    {
        var (client, _, _) = await IngestClientAsync(factory, "ding-excluded");
        var guild = NewSnowflake();
        await DbAsync(db =>
        {
            db.DiscordGuildOptOuts.Add(new DiscordGuildOptOut { GuildId = guild, CreatedAt = DateTime.UtcNow, ByUsername = "admin" });
            return db.SaveChangesAsync();
        });

        using var response = await PostSyncAsync(client, guild, Sync(guild, [Member(NewSnowflake(), "excluded", joinedAt: LongAgo)], durationMs: 0));

        (await ProblemAsync(response, HttpStatusCode.Conflict)).GetProperty("code").GetString().Should().Be("guild_excluded");
        (await DbAsync(db => db.DiscordGuilds.AnyAsync(g => g.GuildId == guild))).Should().BeFalse();
    }

    [Theory]
    [InlineData("route-not-snowflake")]
    [InlineData("guild-id-mismatch")]
    [InlineData("duplicate-user-id")]
    [InlineData("invalid-icon")]
    public async Task InvalidSync_Is400InvalidSync_AndNothingIsStored(string rule)
    {
        var (client, _, _) = await IngestClientAsync(factory, $"ding-400-{rule}");
        var guild = NewSnowflake();
        var user = NewSnowflake();
        var valid = Sync(guild, [Member(user, "invalid", joinedAt: LongAgo)], durationMs: 0);
        var (route, body) = rule switch
        {
            "route-not-snowflake" => ("not-a-snowflake", valid),
            "guild-id-mismatch" => (NewSnowflake(), valid),
            "duplicate-user-id" => (guild, valid with
            {
                Members = [Member(user, "first", joinedAt: LongAgo), Member(user, "second", joinedAt: LongAgo)],
                Coverage = valid.Coverage with { ExpectedCount = 2, CollectedCount = 2 },
            }),
            "invalid-icon" => (guild, valid with { Guild = valid.Guild with { Icon = "../../avatars/x.png" } }),
            _ => throw new ArgumentOutOfRangeException(nameof(rule), rule, null),
        };

        using var response = await PostSyncAsync(client, route, body);

        (await ProblemAsync(response, HttpStatusCode.BadRequest)).GetProperty("code").GetString().Should().Be("invalid_sync");
        (await DbAsync(db => db.DiscordGuilds.AnyAsync(g => g.GuildId == guild || g.GuildId == route))).Should().BeFalse();
    }

    [Fact]
    public async Task UnknownFields_AndNullOptionalFields_AreAccepted()
    {
        var (client, _, _) = await IngestClientAsync(factory, "ding-nulls");
        var guild = NewSnowflake();
        var user = NewSnowflake();
        var json = $$"""
            {
              "pluginVersion": "1.0.0",
              "collectedAt": "2026-09-30T12:00:00Z",
              "collectionDurationMs": 0,
              "sentByALaterPlugin": { "extra": [1, 2, 3] },
              "guild": { "id": "{{guild}}", "name": "Nulls", "icon": null, "memberCount": null, "banner": null },
              "coverage": { "method": "member-search", "complete": true, "expectedCount": null, "collectedCount": 1 },
              "roles": [],
              "members": [
                { "userId": "{{user}}", "username": "nulls", "globalName": null, "nick": null,
                  "roleIds": [], "joinedAt": null, "bot": false, "avatar": "ignored" }
              ]
            }
            """;

        using var response = await client.PostAsync($"/api/ingest/discord/guilds/{guild}/syncs",
            new StringContent(json, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        result.GetProperty("isComplete").GetBoolean().Should().BeFalse("without an expected count nothing proves the list is whole");
        result.GetProperty("membersReceived").GetInt32().Should().Be(1);
    }

    [Theory]
    [InlineData("roles", true)]
    [InlineData("roles", false)]
    [InlineData("members", true)]
    [InlineData("members", false)]
    public async Task MissingOrNullRequiredArray_Is400_Never500(string field, bool presentAsNull)
    {
        var (client, _, _) = await IngestClientAsync(factory, $"ding-array-{field}-{presentAsNull}");
        var guild = NewSnowflake();
        var body = JsonSerializer.SerializeToNode(
            Sync(guild, [Member(NewSnowflake(), "arrays", joinedAt: LongAgo)], durationMs: 0), JsonSerializerOptions.Web)!.AsObject();
        if (presentAsNull) body[field] = null;
        else body.Remove(field);

        using var response = await client.PostAsync($"/api/ingest/discord/guilds/{guild}/syncs",
            new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        (await DbAsync(db => db.DiscordGuilds.AnyAsync(g => g.GuildId == guild))).Should().BeFalse();
    }

    // --- retries and clocks ---

    [Fact]
    public async Task RepostOfACommittedSync_IsStaleOrEventless_NeverDuplicated()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        using var app = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<TimeProvider>(clock)));
        var (_, rawKey, _) = await IngestClientAsync(factory, "ding-repost");
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("x-api-key", rawKey);
        var guild = NewSnowflake();
        var (a, b, c) = (NewSnowflake(), NewSnowflake(), NewSnowflake());
        await PostOkAsync(client, guild, Sync(guild,
            [Member(a, "repost-a", joinedAt: LongAgo), Member(b, "repost-b", joinedAt: LongAgo), Member(c, "repost-c", joinedAt: LongAgo)],
            durationMs: 0));

        clock.Now += TimeSpan.FromMinutes(10);
        var changed = Sync(guild,
            [Member(a, "repost-a", nick: "Renamed", joinedAt: LongAgo), Member(b, "repost-b", joinedAt: LongAgo)],
            durationMs: 60_000);
        ShouldCount(await PostOkAsync(client, guild, changed), left: 1, nickChanged: 1);
        var events = (await GuildEventsAsync(guild)).Count;

        // The plugin got no answer after its upload and retries at once, within its collection time.
        clock.Now += TimeSpan.FromSeconds(5);
        using (var retry = await PostSyncAsync(client, guild, changed))
        {
            (await ProblemAsync(retry, HttpStatusCode.Conflict)).GetProperty("code").GetString().Should().Be("stale_sync");
        }

        // A later retry of the same body: accepted, and nothing left to record.
        clock.Now += TimeSpan.FromMinutes(10);
        ShouldCount(await PostOkAsync(client, guild, changed));
        (await GuildEventsAsync(guild)).Should().HaveCount(events);
        (await DbAsync(db => db.DiscordSyncs.CountAsync(s => s.GuildId == guild))).Should().Be(3);
    }

    [Fact]
    public async Task PluginClockOffByDays_NeitherRefusesNorReordersSyncs()
    {
        var (client, _, _) = await IngestClientAsync(factory, "ding-clock");
        var guild = NewSnowflake();
        var (a, b) = (NewSnowflake(), NewSnowflake());
        var ahead = DateTimeOffset.UtcNow.AddDays(5);
        var behind = DateTimeOffset.UtcNow.AddDays(-30);

        await PostOkAsync(client, guild, Sync(guild,
            [Member(a, "clock-a", joinedAt: LongAgo), Member(b, "clock-b", joinedAt: LongAgo)], durationMs: 0) with { CollectedAt = ahead });
        var second = await PostOkAsync(client, guild, Sync(guild,
            [Member(a, "clock-a", joinedAt: LongAgo)], durationMs: 0) with { CollectedAt = behind });

        ShouldCount(second, left: 1);
        var syncs = await DbAsync(db => db.DiscordSyncs.AsNoTracking().Where(s => s.GuildId == guild).OrderBy(s => s.Id).ToListAsync());
        syncs[0].DeclaredCollectedAt.Should().BeCloseTo(ahead.UtcDateTime, TimeSpan.FromSeconds(1));
        syncs[1].DeclaredCollectedAt.Should().BeCloseTo(behind.UtcDateTime, TimeSpan.FromSeconds(1));
        syncs.Should().AllSatisfy(s => s.CollectedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1)));
        syncs[1].CollectedAt.Should().BeOnOrAfter(syncs[0].CollectedAt);
    }

    // --- contention, load and transport ---

    [Fact]
    public async Task TwoSimultaneousSyncs_AreSerialised_AndShareOneAccountRow()
    {
        var (client, _, _) = await IngestClientAsync(factory, "ding-concurrent");
        var shared = NewSnowflake();
        var guilds = new[] { NewSnowflake(), NewSnowflake() };

        var responses = await Task.WhenAll(guilds.Select(g => PostSyncAsync(client, g, Sync(g,
            [Member(shared, "shared", joinedAt: LongAgo), Member(NewSnowflake(), $"own{g}", joinedAt: LongAgo)], durationMs: 0))));

        responses.Select(r => r.StatusCode).Should()
            .AllSatisfy(s => s.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.ServiceUnavailable));
        responses.Should().Contain(r => r.StatusCode == HttpStatusCode.OK, "the second one waits for the gate");
        foreach (var busy in responses.Where(r => r.StatusCode == HttpStatusCode.ServiceUnavailable))
        {
            busy.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromSeconds(30));
        }
        (await DbAsync(db => db.DiscordAccounts.CountAsync(a => a.DiscordUserId == shared))).Should().Be(1);
    }

    [Fact]
    public async Task GateHeldLongerThan10s_Is503WithRetryAfter_ThenARetrySucceeds()
    {
        var (client, _, _) = await IngestClientAsync(factory, "ding-gate-busy");
        var guild = NewSnowflake();
        var body = Sync(guild, [Member(NewSnowflake(), "waiting", joinedAt: LongAgo)], durationMs: 0);
        var gate = factory.Services.GetRequiredService<DiscordWriteGate>();

        using (await gate.EnterAsync(CancellationToken.None))
        {
            using var busy = await PostSyncAsync(client, guild, body);
            (await ProblemAsync(busy, HttpStatusCode.ServiceUnavailable)).GetProperty("code").GetString().Should().Be("busy");
            busy.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromSeconds(30));
        }

        (await PostOkAsync(client, guild, body)).GetProperty("isBaseline").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task TrackerDbLockedByAnotherWriter_Is503Busy_ThenARetrySucceeds()
    {
        var (client, _, _) = await IngestClientAsync(factory, "ding-db-busy");
        client.Timeout = TimeSpan.FromMinutes(5);   // busy_timeout, one retry after 2 s, busy_timeout again
        var guild = NewSnowflake();
        var body = Sync(guild, [Member(NewSnowflake(), "locked", joinedAt: LongAgo)], durationMs: 0);

        // Plays the collector: holds the write lock of tracker.db until the API has given up.
        await using (var collector = new SqliteConnection($"Data Source={Path.Combine(factory.DataDir, "tracker.db")};Pooling=False"))
        {
            await collector.OpenAsync();
            await using (var begin = collector.CreateCommand())
            {
                begin.CommandText = "BEGIN IMMEDIATE;";
                await begin.ExecuteNonQueryAsync();
            }
            try
            {
                using var busy = await PostSyncAsync(client, guild, body);
                (await ProblemAsync(busy, HttpStatusCode.ServiceUnavailable)).GetProperty("code").GetString().Should().Be("busy");
                busy.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromSeconds(30));
            }
            finally
            {
                await using var rollback = collector.CreateCommand();
                rollback.CommandText = "ROLLBACK;";
                await rollback.ExecuteNonQueryAsync();
            }
        }

        (await PostOkAsync(client, guild, body)).GetProperty("isBaseline").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task FiftyThousandMemberBaseline_IsStoredInFull()
    {
        var (client, _, _) = await IngestClientAsync(factory, "ding-50k");
        client.Timeout = TimeSpan.FromMinutes(5);
        var guild = NewSnowflake();
        var role = NewSnowflake();
        var members = Enumerable.Range(0, DiscordSyncValidator.MaxMembers)
            .Select(i => Member(NewSnowflake(), $"member{i}", [role], joinedAt: LongAgo))
            .ToList();

        var result = await PostOkAsync(client, guild, Sync(guild, members, [Role(role, "Membre", 1)], durationMs: 0));

        result.GetProperty("isBaseline").GetBoolean().Should().BeTrue();
        result.GetProperty("isComplete").GetBoolean().Should().BeTrue();
        result.GetProperty("membersReceived").GetInt32().Should().Be(DiscordSyncValidator.MaxMembers);
        (await DbAsync(db => db.DiscordMembers.CountAsync(m => m.GuildId == guild))).Should().Be(DiscordSyncValidator.MaxMembers);
        (await DbAsync(db => db.DiscordSyncs.SingleAsync(s => s.GuildId == guild))).CollectedCount.Should().Be(DiscordSyncValidator.MaxMembers);
    }

    [Fact]
    public async Task BodyOverTheLimit_Is413ProblemDetails()
    {
        // TestServer does not enforce [RequestSizeLimit]: this test needs a real Kestrel server.
        var (_, rawKey, _) = await IngestClientAsync(factory, "ding-413");
        using var kestrel = new KestrelFactory();
        kestrel.UseKestrel(0);
        kestrel.StartServer();
        var address = kestrel.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        // Kestrel refuses the body from its Content-Length, before it sends "100 Continue". The
        // client waits for that answer instead of uploading, so the refusal never races the upload.
        using var handler = new SocketsHttpHandler { Expect100ContinueTimeout = TimeSpan.FromSeconds(30) };
        using var client = new HttpClient(handler) { BaseAddress = new Uri(address), Timeout = TimeSpan.FromMinutes(2) };
        client.DefaultRequestHeaders.Add("x-api-key", rawKey);
        var warmUp = NewSnowflake();
        (await PostSyncAsync(client, warmUp, Sync(warmUp, [Member(NewSnowflake(), "warm-up", joinedAt: LongAgo)], durationMs: 0)))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        // Over the action's 25 MiB, under Kestrel's own 30,000,000-byte default: only
        // [RequestSizeLimit] can refuse it.
        var padding = new string('x', (int)DiscordIngestController.MaxBodyBytes + 1024 * 1024);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/ingest/discord/guilds/{NewSnowflake()}/syncs")
        {
            Content = new StringContent($$"""{"pluginVersion":"{{padding}}"}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.ExpectContinue = true;

        using var response = await client.SendAsync(request);

        var problem = await ProblemAsync(response, HttpStatusCode.RequestEntityTooLarge);
        problem.GetProperty("status").GetInt32().Should().Be(413);
        problem.GetProperty("title").GetString().Should().Be("Payload Too Large");
        problem.TryGetProperty("detail", out _).Should().BeFalse("the framework message is only logged");
        problem.TryGetProperty("code", out _).Should().BeFalse("a 413 carries no Discord code");
    }

    [Fact]
    public async Task IngestBudget_Is429WithRetryAfter()
    {
        var (_, rawKey, _) = await IngestClientAsync(factory, "ding-429");
        using var app = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.PostConfigure<RateLimitSettings>(limits => limits.DiscordIngest.PermitLimit = 2)));
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("x-api-key", rawKey);
        var guild = NewSnowflake();
        var body = Sync(guild, [Member(NewSnowflake(), "limited", joinedAt: LongAgo)], durationMs: 0);

        (await PostSyncAsync(client, guild, body)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await PostSyncAsync(client, guild, body)).StatusCode.Should().Be(HttpStatusCode.OK);
        using var limited = await PostSyncAsync(client, guild, body);

        limited.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        limited.Headers.RetryAfter.Should().NotBeNull();
        limited.Headers.RetryAfter!.Delta.Should().BePositive();
    }

    // --- helpers ---

    /// <summary>A clock the test moves by hand; only <c>GetUtcNow</c> is used by the ingest route.</summary>
    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>
    /// The real API on Kestrel. It reuses the data directory and secrets the shared ApiFactory
    /// put in the environment.
    /// </summary>
    private sealed class KestrelFactory : WebApplicationFactory<Program>
    {
    }

    private static DateTime Second(DateTimeOffset value)
    {
        var utc = value.UtcDateTime;
        return new DateTime(utc.Ticks - utc.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
    }

    private static async Task<JsonElement> PostOkAsync(HttpClient client, string guildId, DiscordSyncRequest body)
    {
        using var response = await PostSyncAsync(client, guildId, body);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        response.StatusCode.Should().Be(status, await response.Content.ReadAsStringAsync());
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static void ShouldCount(JsonElement result, int joined = 0, int left = 0, int rejoined = 0,
        int rolesChanged = 0, int nickChanged = 0, int nameChanged = 0)
    {
        var events = result.GetProperty("events");
        var actual = new[] { "joined", "left", "rejoined", "rolesChanged", "nickChanged", "nameChanged" }
            .ToDictionary(name => name, name => events.GetProperty(name).GetInt32());
        actual.Should().Equal(new Dictionary<string, int>
        {
            ["joined"] = joined,
            ["left"] = left,
            ["rejoined"] = rejoined,
            ["rolesChanged"] = rolesChanged,
            ["nickChanged"] = nickChanged,
            ["nameChanged"] = nameChanged,
        });
    }

    private async Task<HttpClient> ClientWithAsync(string credential, string username)
    {
        switch (credential)
        {
            case "none":
                return factory.CreateClient();
            case "jwt":
                return await factory.SignedInClientAsync(username);
            case "admin-static-key":
                return KeyClient(ApiFactory.AdminApiKey);
            case "full-key":
            {
                var owner = await factory.SignedInClientAsync(username);
                var created = await owner.PostAsJsonAsync("/api/api-keys", new { name = "full", expiresAt = (DateTime?)null });
                created.EnsureSuccessStatusCode();
                return KeyClient((await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("rawKey").GetString()!);
            }
            case "revoked-key":
            {
                var owner = await factory.SignedInClientAsync(username);
                var created = await owner.PostAsJsonAsync("/api/api-keys",
                    new { name = "revoked", expiresAt = DateTime.UtcNow.AddDays(30), scope = "discord:ingest" });
                created.EnsureSuccessStatusCode();
                var key = await created.Content.ReadFromJsonAsync<JsonElement>();
                (await owner.DeleteAsync($"/api/api-keys/{key.GetProperty("id").GetInt64()}")).EnsureSuccessStatusCode();
                return KeyClient(key.GetProperty("rawKey").GetString()!);
            }
            case "expired-key":
            {
                var (client, _, userId) = await IngestClientAsync(factory, username);
                using var scope = factory.Services.CreateScope();
                await scope.ServiceProvider.GetRequiredService<ApiDbContext>().ApiKeys
                    .Where(k => k.ApiUserId == userId)
                    .ExecuteUpdateAsync(s => s.SetProperty(k => k.ExpiresAt, (DateTime?)DateTime.UtcNow.AddMinutes(-1)));
                return client;
            }
            case "banned-owner":
            {
                var (client, _, userId) = await IngestClientAsync(factory, username);
                using var admin = KeyClient(ApiFactory.AdminApiKey);
                (await admin.PutAsJsonAsync($"/api/admin/users/{userId}", new { isBanned = true })).EnsureSuccessStatusCode();
                return client;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(credential), credential, null);
        }
    }

    private HttpClient KeyClient(string rawKey)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("x-api-key", rawKey);
        return client;
    }

    private async Task<T> DbAsync<T>(Func<TrackerDbContext, Task<T>> query)
    {
        using var scope = factory.Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<TrackerDbContext>());
    }

    private Task<List<DiscordMemberEvent>> GuildEventsAsync(string guildId) =>
        DbAsync(db => db.DiscordMemberEvents.AsNoTracking().Where(e => e.GuildId == guildId).OrderBy(e => e.Id).ToListAsync());

    private Task<List<DiscordMemberEvent>> AccountEventsAsync(string userId) =>
        DbAsync(db => db.DiscordMemberEvents.AsNoTracking()
            .Where(e => e.GuildId == null && e.DiscordUserId == userId).OrderBy(e => e.Id).ToListAsync());
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test src/Collector.Api.Tests --filter "FullyQualifiedName~DiscordIngestTests"`

Expected: the build fails with `error CS0103: The name 'DiscordIngestController' does not exist in the current context`.

- [ ] **Step 3: Write the minimal implementation**

Create `src/Collector.Api/Services/Discord/DiscordIngestService.cs`:

```csharp
using Collector.Api.Dtos.Discord;
using Collector.Api.Errors;
using Collector.Data.Repositories;
using Collector.Discord;
using Collector.Models;

namespace Collector.Api.Services.Discord;

/// <summary>
/// Turns a plugin sync into roster rows and events (spec § 9.1 steps 2–6). It runs while the
/// request holds the Discord write gate (<see cref="DiscordIngestGateFilter"/>), so no other
/// Discord writer can slip between the stale check, the snapshot, the diff and the writes.
/// </summary>
public sealed class DiscordIngestService
{
    /// <summary>Seconds a writer is told to wait when tracker.db stays busy.</summary>
    public const int BusyRetryAfterSeconds = 30;

    private readonly IDiscordRosterRepository _roster;
    private readonly ILogger<DiscordIngestService> _logger;

    public DiscordIngestService(IDiscordRosterRepository roster, ILogger<DiscordIngestService> logger)
    {
        _roster = roster;
        _logger = logger;
    }

    public async Task<DiscordSyncResponseDto> IngestAsync(
        string guildId, DiscordSyncRequest request, DateTime receivedAt,
        long submitterId, string submitterName, CancellationToken ct)
    {
        var sync = DiscordSyncValidator.Normalize(guildId, request);

        // The plugin's clock is only informative. The sync is dated from the server's reception
        // minus the measured collection time, which is when the collection started.
        var collectedAt = receivedAt - sync.CollectionDuration;

        // A sync accepted after this collection started may already hold newer data.
        var lastReceivedAt = await _roster.GetLastSyncReceivedAtAsync(guildId, ct);
        if (lastReceivedAt > collectedAt)
            throw new ConflictException("Un envoi plus récent a été reçu pendant ta collecte.", DiscordErrorCodes.StaleSync);

        var userIds = sync.Members.Select(m => m.UserId).ToList();
        var snapshot = await _roster.LoadSnapshotAsync(guildId, userIds, ct);
        var optedOut = await _roster.GetOptedOutAsync(userIds, ct);
        var plan = DiscordRosterDiff.Compute(guildId, sync, collectedAt, snapshot, optedOut);

        DiscordSyncResult result;
        try
        {
            result = await _roster.ApplyAsync(
                new DiscordSyncWrite(sync, plan, collectedAt, receivedAt, submitterId, submitterName), ct);
        }
        catch (DiscordStoreBusyException ex)
        {
            // The transactions already committed stay valid; the next sync completes the rest.
            _logger.LogWarning(ex, "tracker.db stayed busy while storing a sync of guild {GuildId}", guildId);
            throw new ServiceUnavailableException(
                "Base du tracker occupée, réessaie dans 30 s.", BusyRetryAfterSeconds, DiscordErrorCodes.Busy);
        }

        return new DiscordSyncResponseDto
        {
            SyncId = result.SyncId,
            IsBaseline = plan.IsBaseline,
            IsComplete = plan.IsComplete,
            DepartureGuardTripped = plan.DepartureGuardTripped,
            OrgSid = result.OrgSid,
            MembersReceived = sync.CollectedCount,
            MembersOptedOut = plan.OptedOutCount,
            UnknownRoleRefs = sync.UnknownRoleRefCount,
            Events = CountEvents(plan.Events),
        };
    }

    private static DiscordSyncEventCountsDto CountEvents(IReadOnlyList<PlannedEvent> events)
    {
        var counts = new DiscordSyncEventCountsDto();
        foreach (var e in events)
        {
            switch (e.Type)
            {
                case DiscordEventTypes.Joined: counts.Joined++; break;
                case DiscordEventTypes.Left: counts.Left++; break;
                case DiscordEventTypes.Rejoined: counts.Rejoined++; break;
                case DiscordEventTypes.RolesChanged: counts.RolesChanged++; break;
                case DiscordEventTypes.NickChanged: counts.NickChanged++; break;
                case DiscordEventTypes.UsernameChanged:
                case DiscordEventTypes.GlobalNameChanged: counts.NameChanged++; break;
            }
        }
        return counts;
    }
}
```

Create `src/Collector.Api/Controllers/DiscordIngestController.cs`:

```csharp
using Collector.Api.Auth;
using Collector.Api.Dtos.Discord;
using Collector.Api.Errors;
using Collector.Api.Extensions;
using Collector.Api.Services.Discord;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Collector.Api.Controllers;

/// <summary>
/// The only API route reachable from outside the VPS (nginx <c>/ingest/discord/</c>). The
/// Vencord plugin posts a guild roster here with a discord:ingest key. The gate filter
/// validates the guild id, refuses excluded guilds and holds the Discord write gate around
/// model binding and the action, so the body is read only once the request may write.
/// </summary>
[ApiController]
[Route("api/ingest/discord")]
[Authorize(Policy = DiscordIngestAuth.PolicyName)]
[EnableRateLimiting(RateLimitingExtensions.DiscordIngestPolicy)]
public class DiscordIngestController : ControllerBase
{
    /// <summary>25 MiB: about 50,000 members at worst-case field lengths (spec § 6.3).</summary>
    public const long MaxBodyBytes = 25L * 1024 * 1024;

    private readonly DiscordIngestService _ingest;
    private readonly CurrentUserAccessor _currentUser;
    private readonly TimeProvider _time;

    public DiscordIngestController(DiscordIngestService ingest, CurrentUserAccessor currentUser, TimeProvider time)
    {
        _ingest = ingest;
        _currentUser = currentUser;
        _time = time;
    }

    /// <summary>
    /// Stores one roster sync. The reception time is read here, under the gate, so the accepted
    /// syncs of a guild get increasing ReceivedAt values in commit order.
    /// </summary>
    [HttpPost("guilds/{guildId}/syncs")]
    [RequestSizeLimit(MaxBodyBytes)]
    [TypeFilter(typeof(DiscordIngestGateFilter))]
    public async Task<ActionResult<DiscordSyncResponseDto>> PostSync(
        string guildId, [FromBody] DiscordSyncRequest request, CancellationToken ct)
    {
        var receivedAt = _time.GetUtcNow().UtcDateTime;
        var submitterId = _currentUser.UserId ?? throw new AuthenticationFailedException("Authentication required");
        var submitterName = _currentUser.Username ?? "unknown";
        return Ok(await _ingest.IngestAsync(guildId, request, receivedAt, submitterId, submitterName, ct));
    }
}
```

In `src/Collector.Api/Extensions/ServiceCollectionExtensions.cs`, method `AddApiServices`, replace:

```csharp
        services.AddSingleton<Collector.Api.Services.Discord.DiscordWriteGate>();
```

with:

```csharp
        services.AddSingleton<Collector.Api.Services.Discord.DiscordWriteGate>();
        services.AddScoped<Collector.Api.Services.Discord.DiscordIngestService>();
        // The ingest route dates a sync from its reception; tests swap this clock.
        services.AddSingleton(TimeProvider.System);
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test src/Collector.Api.Tests --filter "FullyQualifiedName~DiscordIngestTests"`

Expected: `Passed!  - Failed:     0, Passed:    42, Skipped:     0, Total:    42`. The run takes about a minute or more. `GateHeldLongerThan10s…` waits for the 10 s gate timeout, and `TrackerDbLockedByAnotherWriter…` waits until the repository gives up on the locked database.

Run the whole API suite once, since the new route joins the anonymous-access enumeration: `dotnet test src/Collector.Api.Tests`

Expected: `Passed!` with `Failed:     0`.

- [ ] **Step 5: Commit**

```bash
git add src/Collector.Api/Services/Discord/DiscordIngestService.cs \
  src/Collector.Api/Controllers/DiscordIngestController.cs \
  src/Collector.Api/Extensions/ServiceCollectionExtensions.cs \
  src/Collector.Api.Tests/Discord/DiscordTestKit.cs \
  src/Collector.Api.Tests/Discord/DiscordIngestTests.cs
git commit -m "$(cat <<'EOF'
feat(api): the Vencord plugin can post a guild roster to the ingest route

This is the one public route of the API. A sync is dated from the server's
reception minus the collection time, never from the PC clock. A sync that
started before the last accepted one is refused as stale, so a plugin retry
after a lost answer never duplicates events. A tracker.db locked by the
collector yields 503 with Retry-After instead of a 500. The tests pin the
access matrix and every roster rule of spec § 9, plus the 413, 429 and
50,000-member cases.

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
EOF
)"
```

### Task A12: DiscordAdminController and DiscordAudit

**Files:**
- Create: `src/Collector.Api/Services/Discord/DiscordAudit.cs`
- Create: `src/Collector.Api/Dtos/Discord/DiscordAdminDtos.cs`
- Create: `src/Collector.Api/Controllers/DiscordAdminController.cs`
- Test: `src/Collector.Api.Tests/Discord/DiscordAuditTests.cs`
- Test: `src/Collector.Api.Tests/Discord/DiscordAdminTests.cs`

**Interfaces:**
- Consumes:
  - CONTRACTS § 3: `IDiscordErasureRepository`, all seven methods:
    - `EraseAccountAsync(string discordUserId, long? byApiUserId, string byUsername, CancellationToken ct = default)`;
    - `EraseGuildAsync(string guildId, bool exclude, long? byApiUserId, string byUsername, CancellationToken ct = default) : Task<bool>`;
    - `ListOptOutsAsync`, `RemoveOptOutAsync`, `ListGuildOptOutsAsync`, `RemoveGuildOptOutAsync`, `AllowMassDepartureOnceAsync`.
  - CONTRACTS § 1: the entities `DiscordOptOut` and `DiscordGuildOptOut`.
  - CONTRACTS § 0 and § 2: `DiscordSnowflake.IsValid` and `DiscordStoreBusyException`.
  - CONTRACTS § 4: `ServiceUnavailableException(string, int, string? = "busy")` and `ValidationException(string, string? = null)`.
  - Task A10: `DiscordWriteGate.TryEnterAsync` (controller) and `DiscordWriteGate.EnterAsync` (the test that plays an ingestion in progress). Task A11: `DiscordTestKit`.
  - Existing code: `ActivityLogService.LogAsync(action, userId, entityType, entityId, ipAddress, ct)` and `CurrentUserAccessor`.
- Produces:
  - `public static Task DiscordAudit.LogAsync(ActivityLogService logs, CurrentUserAccessor user, ILogger logger, string action, string entityType, string entityId, CancellationToken ct)`:
    - it passes `userId = UserId > 0 ? UserId : null` and never throws;
    - action constants: `EraseAccount = "discord_erase_account"`, `EraseGuild = "discord_erase_guild"`, `RemoveOptOut = "discord_remove_optout"`, `RemoveGuildOptOut = "discord_remove_guild_optout"`, `AllowMassDeparture = "discord_allow_mass_departure"`;
    - lot C adds `discord_map_org` and `discord_update_role` beside them.
  - `DiscordOptOutDto { DiscordUserId, CreatedAt, ByUsername, Reason }` and `DiscordGuildOptOutDto { GuildId, CreatedAt, ByUsername, Reason }`, in `Dtos/Discord/DiscordAdminDtos.cs`.
  - `DiscordAdminController`, `[ApiController, Route("api/discord"), Authorize(Policy = "AdminOnly")]`, with the seven routes of CONTRACTS § 5.
  - Decisions:
    - Only the five **writing** actions take the gate (10 s, else 503 `busy`, `Retry-After: 30`). The two GET lists are plain reads.
    - `exclude` is required on `DELETE guilds/{guildId}`: without it the route answers 400.
    - A path id that is not a snowflake is a 400 without `code`.
    - The audit entity types are `discord_account` and `discord_guild`, and the entity id is the snowflake.
    - The audit is written after the lease is released, with `CancellationToken.None`.
    - The actor name falls back to `"admin"`.
    - `DiscordStoreBusyException` thrown by an erasure becomes a 503.

- [ ] **Step 1: Write the failing test**

Create `src/Collector.Api.Tests/Discord/DiscordAuditTests.cs`:

```csharp
using System.Security.Claims;
using Collector.Api.Auth;
using Collector.Api.Data;
using Collector.Api.Models;
using Collector.Api.Services;
using Collector.Api.Services.Discord;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Collector.Api.Tests.Discord;

/// <summary>
/// Spec § 13.2: Discord admin actions are logged after they commit, the static admin key is
/// logged without a user, and a failing log never fails the action.
/// </summary>
public sealed class DiscordAuditTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private ApiDbContext _db = null!;

    public async Task InitializeAsync()
    {
        _connection.Open();
        _db = new ApiDbContext(new DbContextOptionsBuilder<ApiDbContext>().UseSqlite(_connection).Options);
        await _db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        _connection.Dispose();
    }

    private static CurrentUserAccessor Actor(string id, string name) => new(new HttpContextAccessor
    {
        HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, id), new Claim(ClaimTypes.Name, name)], "test")),
        },
    });

    [Fact]
    public async Task StaticAdminKey_IsLoggedWithoutAUser()
    {
        await DiscordAudit.LogAsync(new ActivityLogService(_db), Actor("0", "admin"), new ListLogger(),
            DiscordAudit.EraseAccount, "discord_account", "300000000000000001", CancellationToken.None);

        var log = await _db.ActivityLogs.AsNoTracking().SingleAsync();
        log.ApiUserId.Should().BeNull("the static admin key has no api_users row");
        log.Action.Should().Be("discord_erase_account");
        log.EntityType.Should().Be("discord_account");
        log.EntityId.Should().Be("300000000000000001");
    }

    [Fact]
    public async Task SignedInAdmin_IsLoggedWithTheirId()
    {
        var admin = new ApiUser
        {
            Username = "audit-admin", Email = "audit-admin@example.test", PasswordHash = "x", IsAdmin = true,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        _db.ApiUsers.Add(admin);
        await _db.SaveChangesAsync();

        await DiscordAudit.LogAsync(new ActivityLogService(_db), Actor(admin.Id.ToString(), admin.Username), new ListLogger(),
            DiscordAudit.AllowMassDeparture, "discord_guild", "123456789012345678", CancellationToken.None);

        (await _db.ActivityLogs.AsNoTracking().SingleAsync()).ApiUserId.Should().Be(admin.Id);
    }

    [Fact]
    public async Task FailedWrite_IsLoggedAsAWarning_AndNeverThrows()
    {
        // A fresh in-memory database on every open: activity_logs does not exist there.
        await using var broken = new ApiDbContext(new DbContextOptionsBuilder<ApiDbContext>().UseSqlite("DataSource=:memory:").Options);
        var logger = new ListLogger();

        var act = () => DiscordAudit.LogAsync(new ActivityLogService(broken), Actor("0", "admin"), logger,
            DiscordAudit.EraseGuild, "discord_guild", "123456789012345678", CancellationToken.None);

        await act.Should().NotThrowAsync();
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning);
    }

    private sealed class ListLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception)));
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test src/Collector.Api.Tests --filter "FullyQualifiedName~DiscordAuditTests"`

Expected: the build fails with `error CS0103: The name 'DiscordAudit' does not exist in the current context`.

- [ ] **Step 3: Write the minimal implementation**

Create `src/Collector.Api/Services/Discord/DiscordAudit.cs`:

```csharp
using Collector.Api.Auth;

namespace Collector.Api.Services.Discord;

/// <summary>
/// activity_logs entries of the Discord admin actions (spec § 13.2), written after the
/// tracker.db transactions have committed. The static admin key has no api_users row, so its
/// actions are logged without a user. A failed write is logged and never changes the response,
/// because the change it describes has already happened.
/// </summary>
public static class DiscordAudit
{
    public const string EraseAccount = "discord_erase_account";
    public const string EraseGuild = "discord_erase_guild";
    public const string RemoveOptOut = "discord_remove_optout";
    public const string RemoveGuildOptOut = "discord_remove_guild_optout";
    public const string AllowMassDeparture = "discord_allow_mass_departure";

    public static async Task LogAsync(ActivityLogService logs, CurrentUserAccessor user, ILogger logger,
        string action, string entityType, string entityId, CancellationToken ct)
    {
        try
        {
            var userId = user.UserId > 0 ? user.UserId : null;
            await logs.LogAsync(action, userId, entityType, entityId, user.IpAddress, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not write the {Action} audit entry for {EntityType} {EntityId}",
                action, entityType, entityId);
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test src/Collector.Api.Tests --filter "FullyQualifiedName~DiscordAuditTests"`

Expected: `Passed!  - Failed:     0, Passed:     3, Skipped:     0, Total:     3`

- [ ] **Step 5: Write the failing test for the controller**

Create `src/Collector.Api.Tests/Discord/DiscordAdminTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Collector.Api.Data;
using Collector.Api.Dtos.Discord;
using Collector.Api.Models;
using Collector.Api.Services.Discord;
using Collector.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Collector.Api.Tests.Discord.DiscordTestKit;

namespace Collector.Api.Tests.Discord;

/// <summary>
/// Spec § 13.2 and § 9.3: admin erasure, exclusion and the one-off lift of the mass-departure
/// guard, as seen from the next sync. Each write is audited, and the static key is audited
/// without a user.
/// </summary>
[Collection(ApiCollection.Name)]
public class DiscordAdminTests(ApiFactory factory)
{
    private static readonly DateTimeOffset LongAgo = DateTimeOffset.UtcNow.AddDays(-400);

    [Theory]
    [InlineData("DELETE", "accounts/{id}")]
    [InlineData("DELETE", "guilds/{id}?exclude=false")]
    [InlineData("GET", "optouts")]
    [InlineData("DELETE", "optouts/{id}")]
    [InlineData("GET", "guild-optouts")]
    [InlineData("DELETE", "guild-optouts/{id}")]
    [InlineData("POST", "guilds/{id}/allow-mass-departure")]
    public async Task NonAdmins_AreRefused(string method, string route)
    {
        var path = "/api/discord/" + route.Replace("{id}", NewSnowflake());
        var user = await factory.SignedInClientAsync($"dadm-user-{NewSnowflake()}");
        var (ingest, _, _) = await IngestClientAsync(factory, $"dadm-key-{NewSnowflake()}");

        (await user.SendAsync(new HttpRequestMessage(new HttpMethod(method), path)))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ingest.SendAsync(new HttpRequestMessage(new HttpMethod(method), path)))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized, "a discord:ingest key is authenticated nowhere else");
    }

    [Fact]
    public async Task EraseAccount_RemovesItsRows_AndLaterSyncsIgnoreIt()
    {
        var (admin, adminId) = await AdminAsync("dadm-erase-account");
        var (ingest, _, _) = await IngestClientAsync(factory, "dadm-erase-account-key");
        var guild = NewSnowflake();
        var (erased, kept) = (NewSnowflake(), NewSnowflake());
        var members = new[] { Member(erased, "erased", nick: "Erased", joinedAt: LongAgo), Member(kept, "kept", joinedAt: LongAgo) };
        await PostOkAsync(ingest, guild, Sync(guild, members, durationMs: 0));
        await PostOkAsync(ingest, guild, Sync(guild, [members[0] with { Nick = "Renamed" }, members[1]], durationMs: 0));

        (await admin.DeleteAsync($"/api/discord/accounts/{erased}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await TrackerAsync(db => db.DiscordMembers.AnyAsync(m => m.DiscordUserId == erased))).Should().BeFalse();
        (await TrackerAsync(db => db.DiscordAccounts.AnyAsync(a => a.DiscordUserId == erased))).Should().BeFalse();
        (await TrackerAsync(db => db.DiscordMemberEvents.AnyAsync(e => e.DiscordUserId == erased))).Should().BeFalse();
        var optOut = await TrackerAsync(db => db.DiscordOptOuts.AsNoTracking().SingleAsync(o => o.DiscordUserId == erased));
        optOut.ByApiUserId.Should().Be(adminId);
        optOut.ByUsername.Should().Be("dadm-erase-account");
        (await AuditAsync(DiscordAudit.EraseAccount, erased)).Should().ContainSingle().Which.ApiUserId.Should().Be(adminId);

        var again = await PostOkAsync(ingest, guild, Sync(guild, members, durationMs: 0));

        again.GetProperty("membersOptedOut").GetInt32().Should().Be(1);
        again.GetProperty("isComplete").GetBoolean().Should().BeTrue();
        (await TrackerAsync(db => db.DiscordAccounts.AnyAsync(a => a.DiscordUserId == erased)))
            .Should().BeFalse("an erased account stays out of every later sync");
        var list = await admin.GetFromJsonAsync<JsonElement>("/api/discord/optouts");
        list.EnumerateArray().Should().Contain(o =>
            o.GetProperty("discordUserId").GetString() == erased && o.GetProperty("byUsername").GetString() == "dadm-erase-account");
    }

    [Fact]
    public async Task EraseAccount_DuringAnIngestion_IsNeverUndoneByIt()
    {
        // Spec § 15 RGPD. Both writes take the Discord write gate, so whichever runs second sees
        // the other's commit: the ingestion skips an opted-out account, or the erasure removes
        // what the ingestion wrote. Either order ends with the account erased and opted out.
        var (admin, _) = await AdminAsync("dadm-erase-race");
        var (ingest, _, _) = await IngestClientAsync(factory, "dadm-erase-race-key");
        var guild = NewSnowflake();
        var user = NewSnowflake();
        var body = Sync(guild,
            [Member(user, "racing", joinedAt: LongAgo), Member(NewSnowflake(), "bystander", joinedAt: LongAgo)], durationMs: 0);
        await PostOkAsync(ingest, guild, body);
        var gate = factory.Services.GetRequiredService<DiscordWriteGate>();
        Task<HttpResponseMessage> erase;
        Task<HttpResponseMessage> repost;

        // Plays an ingestion in progress: both requests queue behind it.
        using (await gate.EnterAsync(CancellationToken.None))
        {
            erase = admin.DeleteAsync($"/api/discord/accounts/{user}");
            repost = PostSyncAsync(ingest, guild, body);
        }

        using (var erased = await erase)
        {
            erased.StatusCode.Should().Be(HttpStatusCode.NoContent);
        }
        using (var reposted = await repost)
        {
            reposted.StatusCode.Should().Be(HttpStatusCode.OK, await reposted.Content.ReadAsStringAsync());
        }
        (await TrackerAsync(db => db.DiscordAccounts.AnyAsync(a => a.DiscordUserId == user))).Should().BeFalse();
        (await TrackerAsync(db => db.DiscordMembers.AnyAsync(m => m.DiscordUserId == user))).Should().BeFalse();
        (await TrackerAsync(db => db.DiscordOptOuts.AnyAsync(o => o.DiscordUserId == user))).Should().BeTrue();
    }

    [Fact]
    public async Task EraseGuild_WithExclusion_RefusesLaterSyncs_UntilTheExclusionIsLifted()
    {
        var (admin, adminId) = await AdminAsync("dadm-exclude");
        var (ingest, _, _) = await IngestClientAsync(factory, "dadm-exclude-key");
        var guild = NewSnowflake();
        var body = Sync(guild, [Member(NewSnowflake(), "excluded", joinedAt: LongAgo)], durationMs: 0);
        await PostOkAsync(ingest, guild, body);

        (await admin.DeleteAsync($"/api/discord/guilds/{guild}?exclude=true")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await TrackerAsync(db => db.DiscordGuilds.AnyAsync(g => g.GuildId == guild))).Should().BeFalse();
        (await TrackerAsync(db => db.DiscordMembers.AnyAsync(m => m.GuildId == guild))).Should().BeFalse();
        (await TrackerAsync(db => db.DiscordSyncs.AnyAsync(s => s.GuildId == guild))).Should().BeFalse();
        (await AuditAsync(DiscordAudit.EraseGuild, guild)).Should().ContainSingle().Which.ApiUserId.Should().Be(adminId);
        using (var refused = await PostSyncAsync(ingest, guild, body))
        {
            refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString().Should().Be("guild_excluded");
        }
        var excluded = await admin.GetFromJsonAsync<JsonElement>("/api/discord/guild-optouts");
        excluded.EnumerateArray().Should().Contain(o => o.GetProperty("guildId").GetString() == guild);

        (await admin.DeleteAsync($"/api/discord/guild-optouts/{guild}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await AuditAsync(DiscordAudit.RemoveGuildOptOut, guild)).Should().ContainSingle();
        (await PostOkAsync(ingest, guild, body)).GetProperty("isBaseline").GetBoolean().Should().BeTrue();
        (await admin.DeleteAsync($"/api/discord/guild-optouts/{guild}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task EraseGuild_WithoutExclusion_TheNextSyncIsANewBaseline()
    {
        var (admin, _) = await AdminAsync("dadm-restart");
        var (ingest, _, _) = await IngestClientAsync(factory, "dadm-restart-key");
        var guild = NewSnowflake();
        var (stays, leaves) = (NewSnowflake(), NewSnowflake());
        await PostOkAsync(ingest, guild, Sync(guild,
            [Member(stays, "stays", joinedAt: LongAgo), Member(leaves, "leaves", joinedAt: LongAgo)], durationMs: 0));
        await PostOkAsync(ingest, guild, Sync(guild, [Member(stays, "stays", joinedAt: LongAgo)], durationMs: 0));

        (await admin.DeleteAsync($"/api/discord/guilds/{guild}?exclude=false")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await TrackerAsync(db => db.DiscordMemberEvents.AnyAsync(e => e.GuildId == guild))).Should().BeFalse();
        (await TrackerAsync(db => db.DiscordAccounts.AnyAsync(a => a.DiscordUserId == stays || a.DiscordUserId == leaves)))
            .Should().BeFalse("unlinked accounts left without any member row go with the guild");
        (await TrackerAsync(db => db.DiscordGuildOptOuts.AnyAsync(o => o.GuildId == guild))).Should().BeFalse();

        var restarted = await PostOkAsync(ingest, guild, Sync(guild, [Member(stays, "stays", joinedAt: LongAgo)], durationMs: 0));

        restarted.GetProperty("isBaseline").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task EraseGuild_OfAnUnknownGuild_Is404_UnlessItIsBeingExcluded()
    {
        var (admin, _) = await AdminAsync("dadm-unknown-guild");
        var (ingest, _, _) = await IngestClientAsync(factory, "dadm-unknown-guild-key");
        var unknown = NewSnowflake();

        (await admin.DeleteAsync($"/api/discord/guilds/{unknown}?exclude=false")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await AuditAsync(DiscordAudit.EraseGuild, unknown)).Should().BeEmpty();

        (await admin.DeleteAsync($"/api/discord/guilds/{unknown}?exclude=true")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        using var refused = await PostSyncAsync(ingest, unknown,
            Sync(unknown, [Member(NewSnowflake(), "early", joinedAt: LongAgo)], durationMs: 0));
        refused.StatusCode.Should().Be(HttpStatusCode.Conflict, "a guild can be excluded before its first sync");
    }

    [Theory]
    [InlineData("DELETE", "guilds/{id}")]
    [InlineData("DELETE", "guilds/not-a-snowflake?exclude=false")]
    [InlineData("DELETE", "accounts/12345")]
    [InlineData("DELETE", "optouts/abc")]
    [InlineData("DELETE", "guild-optouts/abc")]
    [InlineData("POST", "guilds/abc/allow-mass-departure")]
    public async Task MalformedRequests_Are400(string method, string route)
    {
        var (admin, _) = await AdminAsync($"dadm-bad-{NewSnowflake()}");
        var path = "/api/discord/" + route.Replace("{id}", NewSnowflake());

        (await admin.SendAsync(new HttpRequestMessage(new HttpMethod(method), path)))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task RemovingAnAccountOptOut_LetsLaterSyncsRecordItAgain()
    {
        var (admin, _) = await AdminAsync("dadm-optout");
        var (ingest, _, _) = await IngestClientAsync(factory, "dadm-optout-key");
        var guild = NewSnowflake();
        var user = NewSnowflake();
        var body = Sync(guild, [Member(user, "comeback", joinedAt: LongAgo)], durationMs: 0);
        await PostOkAsync(ingest, guild, body);
        (await admin.DeleteAsync($"/api/discord/accounts/{user}")).EnsureSuccessStatusCode();

        (await admin.DeleteAsync($"/api/discord/optouts/{user}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await AuditAsync(DiscordAudit.RemoveOptOut, user)).Should().ContainSingle();
        (await admin.DeleteAsync($"/api/discord/optouts/{user}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var again = await PostOkAsync(ingest, guild, body);
        again.GetProperty("membersOptedOut").GetInt32().Should().Be(0);
        (await TrackerAsync(db => db.DiscordAccounts.AnyAsync(a => a.DiscordUserId == user))).Should().BeTrue();
    }

    [Fact]
    public async Task AllowMassDeparture_LetsOneCompleteSyncThroughTheGuard()
    {
        var (admin, adminId) = await AdminAsync("dadm-mass");
        var (ingest, _, _) = await IngestClientAsync(factory, "dadm-mass-key");
        var guild = NewSnowflake();
        var members = Enumerable.Range(0, 40).Select(i => Member(NewSnowflake(), $"mass{i}", joinedAt: LongAgo)).ToList();
        await PostOkAsync(ingest, guild, Sync(guild, members, durationMs: 0));
        var remaining = members.Take(28).ToList();
        (await PostOkAsync(ingest, guild, Sync(guild, remaining, durationMs: 0)))
            .GetProperty("departureGuardTripped").GetBoolean().Should().BeTrue();

        (await admin.PostAsync($"/api/discord/guilds/{guild}/allow-mass-departure", null))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await AuditAsync(DiscordAudit.AllowMassDeparture, guild)).Should().ContainSingle().Which.ApiUserId.Should().Be(adminId);
        var allowed = await PostOkAsync(ingest, guild, Sync(guild, remaining, durationMs: 0));
        allowed.GetProperty("departureGuardTripped").GetBoolean().Should().BeFalse();
        allowed.GetProperty("events").GetProperty("left").GetInt32().Should().Be(12);
        (await PostOkAsync(ingest, guild, Sync(guild, remaining.Take(16), durationMs: 0)))
            .GetProperty("departureGuardTripped").GetBoolean().Should().BeTrue("the lift was used up");
        (await admin.PostAsync($"/api/discord/guilds/{NewSnowflake()}/allow-mass-departure", null))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task StaticAdminKey_CanAct_AndIsAuditedWithoutAUser()
    {
        var (ingest, _, _) = await IngestClientAsync(factory, "dadm-static-key");
        var guild = NewSnowflake();
        var user = NewSnowflake();
        await PostOkAsync(ingest, guild, Sync(guild,
            [Member(user, "static", joinedAt: LongAgo), Member(NewSnowflake(), "other", joinedAt: LongAgo)], durationMs: 0));
        using var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Add("x-api-key", ApiFactory.AdminApiKey);

        (await admin.DeleteAsync($"/api/discord/accounts/{user}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await admin.PostAsync($"/api/discord/guilds/{guild}/allow-mass-departure", null))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        var optOut = await TrackerAsync(db => db.DiscordOptOuts.AsNoTracking().SingleAsync(o => o.DiscordUserId == user));
        optOut.ByApiUserId.Should().BeNull();
        optOut.ByUsername.Should().Be("admin");
        (await AuditAsync(DiscordAudit.EraseAccount, user)).Should().ContainSingle().Which.ApiUserId.Should().BeNull();
        (await AuditAsync(DiscordAudit.AllowMassDeparture, guild)).Should().ContainSingle().Which.ApiUserId.Should().BeNull();
    }

    // --- helpers ---

    private async Task<(HttpClient Client, long UserId)> AdminAsync(string username)
    {
        var client = await factory.SignedInClientAsync(username, isAdmin: true);
        var me = await client.GetFromJsonAsync<JsonElement>("/api/auth/me");
        return (client, me.GetProperty("id").GetInt64());
    }

    private static async Task<JsonElement> PostOkAsync(HttpClient client, string guildId, DiscordSyncRequest body)
    {
        using var response = await PostSyncAsync(client, guildId, body);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<T> TrackerAsync<T>(Func<TrackerDbContext, Task<T>> query)
    {
        using var scope = factory.Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<TrackerDbContext>());
    }

    private async Task<List<ActivityLog>> AuditAsync(string action, string entityId)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ApiDbContext>().ActivityLogs.AsNoTracking()
            .Where(l => l.Action == action && l.EntityId == entityId)
            .ToListAsync();
    }
}
```

- [ ] **Step 6: Run it to verify it fails**

Run: `dotnet test src/Collector.Api.Tests --filter "FullyQualifiedName~DiscordAdminTests"`

Expected: it builds, then `Failed!  - Failed:    21, Passed:     0`. Every route is still missing, so every test stops on a 404:
- most stop on an assertion such as `Expected … StatusCode to be HttpStatusCode.Forbidden {value: 403}` (or `NoContent`, `BadRequest`) `, but found HttpStatusCode.NotFound {value: 404}`;
- `RemovingAnAccountOptOut…` stops on `HttpRequestException: Response status code does not indicate success: 404 (Not Found)` from `EnsureSuccessStatusCode`.

- [ ] **Step 7: Write the minimal implementation**

Create `src/Collector.Api/Dtos/Discord/DiscordAdminDtos.cs`:

```csharp
namespace Collector.Api.Dtos.Discord;

/// <summary>A Discord account kept out of every sync, after an erasure or an opposition.</summary>
public sealed class DiscordOptOutDto
{
    public string DiscordUserId { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
    public string ByUsername { get; set; } = null!;
    public string? Reason { get; set; }
}

/// <summary>A guild whose syncs are refused (409 guild_excluded).</summary>
public sealed class DiscordGuildOptOutDto
{
    public string GuildId { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
    public string ByUsername { get; set; } = null!;
    public string? Reason { get; set; }
}
```

Create `src/Collector.Api/Controllers/DiscordAdminController.cs`:

```csharp
using Collector.Api.Auth;
using Collector.Api.Dtos.Discord;
using Collector.Api.Errors;
using Collector.Api.Services;
using Collector.Api.Services.Discord;
using Collector.Data.Repositories;
using Collector.Discord;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Collector.Api.Controllers;

/// <summary>
/// Admin-only erasure and opposition for the Discord rosters (spec § 13.2), plus the one-off
/// lift of the mass-departure guard (§ 9.3). Every write holds the Discord write gate so it
/// never interleaves with an ingestion, and is recorded in activity_logs once committed. Until
/// the lot C pages exist, an admin calls these routes from the VPS with the static key.
/// </summary>
[ApiController]
[Route("api/discord")]
[Authorize(Policy = "AdminOnly")]
public class DiscordAdminController : ControllerBase
{
    private const int RetryAfterSeconds = 30;
    private const string AccountEntity = "discord_account";
    private const string GuildEntity = "discord_guild";
    private static readonly TimeSpan GateTimeout = TimeSpan.FromSeconds(10);

    private readonly IDiscordErasureRepository _erasure;
    private readonly DiscordWriteGate _gate;
    private readonly ActivityLogService _activityLog;
    private readonly CurrentUserAccessor _currentUser;
    private readonly ILogger<DiscordAdminController> _logger;

    public DiscordAdminController(
        IDiscordErasureRepository erasure,
        DiscordWriteGate gate,
        ActivityLogService activityLog,
        CurrentUserAccessor currentUser,
        ILogger<DiscordAdminController> logger)
    {
        _erasure = erasure;
        _gate = gate;
        _activityLog = activityLog;
        _currentUser = currentUser;
        _logger = logger;
    }

    /// <summary>Deletes everything stored about a Discord account and keeps it out of later syncs.</summary>
    [HttpDelete("accounts/{discordUserId}")]
    public async Task<IActionResult> EraseAccount(string discordUserId, CancellationToken ct)
    {
        RequireSnowflake(discordUserId, "discordUserId");
        await UnderGateAsync(async c =>
        {
            await _erasure.EraseAccountAsync(discordUserId, ActorId, ActorName, c);
            return true;
        }, ct);
        await AuditAsync(DiscordAudit.EraseAccount, AccountEntity, discordUserId);
        return NoContent();
    }

    /// <summary>
    /// Deletes a guild's roster and history. <c>exclude=true</c> also refuses its later syncs
    /// (409 guild_excluded). <c>exclude=false</c> makes the next sync a new baseline.
    /// </summary>
    [HttpDelete("guilds/{guildId}")]
    public async Task<IActionResult> EraseGuild(string guildId, [FromQuery, BindRequired] bool exclude, CancellationToken ct)
    {
        RequireSnowflake(guildId, "guildId");
        var erased = await UnderGateAsync(c => _erasure.EraseGuildAsync(guildId, exclude, ActorId, ActorName, c), ct);
        if (!erased) return NotFound();
        await AuditAsync(DiscordAudit.EraseGuild, GuildEntity, guildId);
        return NoContent();
    }

    [HttpGet("optouts")]
    public async Task<ActionResult<IReadOnlyList<DiscordOptOutDto>>> ListOptOuts(CancellationToken ct)
    {
        var rows = await _erasure.ListOptOutsAsync(ct);
        return Ok(rows.Select(o => new DiscordOptOutDto
        {
            DiscordUserId = o.DiscordUserId,
            CreatedAt = o.CreatedAt,
            ByUsername = o.ByUsername,
            Reason = o.Reason,
        }).ToList());
    }

    /// <summary>Lets later syncs record this account again.</summary>
    [HttpDelete("optouts/{discordUserId}")]
    public async Task<IActionResult> RemoveOptOut(string discordUserId, CancellationToken ct)
    {
        RequireSnowflake(discordUserId, "discordUserId");
        var removed = await UnderGateAsync(c => _erasure.RemoveOptOutAsync(discordUserId, c), ct);
        if (!removed) return NotFound();
        await AuditAsync(DiscordAudit.RemoveOptOut, AccountEntity, discordUserId);
        return NoContent();
    }

    [HttpGet("guild-optouts")]
    public async Task<ActionResult<IReadOnlyList<DiscordGuildOptOutDto>>> ListGuildOptOuts(CancellationToken ct)
    {
        var rows = await _erasure.ListGuildOptOutsAsync(ct);
        return Ok(rows.Select(o => new DiscordGuildOptOutDto
        {
            GuildId = o.GuildId,
            CreatedAt = o.CreatedAt,
            ByUsername = o.ByUsername,
            Reason = o.Reason,
        }).ToList());
    }

    /// <summary>Accepts syncs of this guild again; the next one is a baseline.</summary>
    [HttpDelete("guild-optouts/{guildId}")]
    public async Task<IActionResult> RemoveGuildOptOut(string guildId, CancellationToken ct)
    {
        RequireSnowflake(guildId, "guildId");
        var removed = await UnderGateAsync(c => _erasure.RemoveGuildOptOutAsync(guildId, c), ct);
        if (!removed) return NotFound();
        await AuditAsync(DiscordAudit.RemoveGuildOptOut, GuildEntity, guildId);
        return NoContent();
    }

    /// <summary>Lets the next complete sync of this guild record more departures than the guard allows.</summary>
    [HttpPost("guilds/{guildId}/allow-mass-departure")]
    public async Task<IActionResult> AllowMassDeparture(string guildId, CancellationToken ct)
    {
        RequireSnowflake(guildId, "guildId");
        var allowed = await UnderGateAsync(c => _erasure.AllowMassDepartureOnceAsync(guildId, c), ct);
        if (!allowed) return NotFound();
        await AuditAsync(DiscordAudit.AllowMassDeparture, GuildEntity, guildId);
        return NoContent();
    }

    /// <summary>The static admin key (user id 0) has no api_users row.</summary>
    private long? ActorId => _currentUser.UserId > 0 ? _currentUser.UserId : null;

    private string ActorName => _currentUser.Username ?? "admin";

    private static void RequireSnowflake(string value, string name)
    {
        if (!DiscordSnowflake.IsValid(value)) throw new ValidationException($"{name} : snowflake attendu.");
    }

    /// <summary>Runs one write under the Discord write gate: 503 when the gate or tracker.db stays busy.</summary>
    private async Task<T> UnderGateAsync<T>(Func<CancellationToken, Task<T>> write, CancellationToken ct)
    {
        using var lease = await _gate.TryEnterAsync(GateTimeout, ct)
            ?? throw new ServiceUnavailableException("Tracker occupé, réessaie dans 30 s.", RetryAfterSeconds);
        try
        {
            return await write(ct);
        }
        catch (DiscordStoreBusyException)
        {
            throw new ServiceUnavailableException("Base du tracker occupée, réessaie dans 30 s.", RetryAfterSeconds);
        }
    }

    // CancellationToken.None: the change is committed, so its audit entry must not depend on
    // the client staying connected.
    private Task AuditAsync(string action, string entityType, string entityId) =>
        DiscordAudit.LogAsync(_activityLog, _currentUser, _logger, action, entityType, entityId, CancellationToken.None);
}
```

- [ ] **Step 8: Run the tests to verify they pass**

Run: `dotnet test src/Collector.Api.Tests --filter "FullyQualifiedName~DiscordAdminTests"`

Expected: `Passed!  - Failed:     0, Passed:    21, Skipped:     0, Total:    21`

Run: `dotnet test src/Collector.Api.Tests --filter "FullyQualifiedName~AuthorizationTests"`

Expected: `Passed!` with `Failed:     0`. The seven new routes answer 401 to an anonymous caller.

- [ ] **Step 9: Commit**

```bash
git add src/Collector.Api/Services/Discord/DiscordAudit.cs \
  src/Collector.Api/Dtos/Discord/DiscordAdminDtos.cs \
  src/Collector.Api/Controllers/DiscordAdminController.cs \
  src/Collector.Api.Tests/Discord/DiscordAuditTests.cs \
  src/Collector.Api.Tests/Discord/DiscordAdminTests.cs
git commit -m "$(cat <<'EOF'
feat(api): admins erase and exclude Discord accounts and guilds

The GDPR erasure and opposition of spec § 13.2 ship with lot A, before any
page exists. An admin can call them from the VPS with the static key. Each
write takes the Discord write gate so it never interleaves with an
ingestion. It is audited once committed, without a user for the static key,
which has no api_users row. The same controller lifts the mass-departure
guard for one complete sync.

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
EOF
)"
```
