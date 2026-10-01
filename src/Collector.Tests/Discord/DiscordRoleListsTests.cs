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
