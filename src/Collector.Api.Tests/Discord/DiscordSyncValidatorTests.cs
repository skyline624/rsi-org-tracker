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
