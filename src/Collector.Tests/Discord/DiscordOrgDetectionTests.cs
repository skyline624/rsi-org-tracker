using Collector.Discord;
using FluentAssertions;
using Xunit;

namespace Collector.Tests.Discord;

/// <summary>
/// The corpo proposed for an unmapped server: the known tag carried by at least three active
/// members and by at least half of the members carrying a known tag; a tie proposes nothing.
/// </summary>
public class DiscordOrgDetectionTests
{
    private static IReadOnlyCollection<string>[] Members(params string[][] tags) => tags;

    [Fact]
    public void ADominantTag_IsProposed_WithItsCounts()
        => DiscordOrgDetection.Pick(Members(["ABC"], ["ABC"], ["ABC"], ["XYZ"], []))
            .Should().Be(new DetectedOrg("ABC", Members: 3, TaggedMembers: 4));

    [Fact]
    public void FewerThanThreeMembers_ProposeNothing()
        => DiscordOrgDetection.Pick(Members(["ABC"], ["ABC"])).Should().BeNull();

    [Fact]
    public void ExactlyHalfOfTheTaggedMembers_IsEnough()
        => DiscordOrgDetection.Pick(Members(["ABC"], ["ABC"], ["ABC"], ["XYZ"], ["DEF"], ["GHI"]))
            .Should().Be(new DetectedOrg("ABC", Members: 3, TaggedMembers: 6));

    [Fact]
    public void LessThanHalfOfTheTaggedMembers_ProposesNothing()
        => DiscordOrgDetection.Pick(Members(["ABC"], ["ABC"], ["ABC"], ["XYZ"], ["DEF"], ["GHI"], ["JKL"]))
            .Should().BeNull();

    [Fact]
    public void ATieBetweenTheTopTags_ProposesNothing()
        => DiscordOrgDetection.Pick(Members(["ABC"], ["ABC"], ["ABC"], ["XYZ"], ["XYZ"], ["XYZ"])).Should().BeNull();

    [Fact]
    public void AMemberWithTwoTags_CountsForBoth_ButOnceAsTagged()
        => DiscordOrgDetection.Pick(Members(["ABC", "XYZ"], ["ABC"], ["ABC"], ["XYZ"]))
            .Should().Be(new DetectedOrg("ABC", Members: 3, TaggedMembers: 4));

    [Fact]
    public void UntaggedMembers_DoNotDilute_TheShare()
        => DiscordOrgDetection.Pick(Members(["ABC"], ["ABC"], ["ABC"], [], [], [], [], []))
            .Should().Be(new DetectedOrg("ABC", Members: 3, TaggedMembers: 3));

    [Fact]
    public void NoTaggedMember_ProposesNothing()
        => DiscordOrgDetection.Pick(Members([], [])).Should().BeNull();
}
