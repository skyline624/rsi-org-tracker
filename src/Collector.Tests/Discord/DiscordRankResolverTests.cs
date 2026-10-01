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
