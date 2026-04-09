using Collector.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Collector.Tests.Services;

/// <summary>
/// Regression tests for the post-audit fix on ChangeDetector: when a snapshot contains
/// duplicate citizen_ids we must take the LAST occurrence (freshest) rather than
/// First(), otherwise rank/role changes between the two duplicate entries are lost.
/// </summary>
public class ChangeDetectorDuplicateTests
{
    private readonly ChangeDetector _sut = new(NullLogger<ChangeDetector>.Instance);

    [Fact]
    public void DetectMemberChanges_DuplicateCitizenIdInCurrent_UsesLatestForRankComparison()
    {
        // Previous snapshot: one entry at rank "Member"
        var previous = new[]
        {
            new MemberSnapshot { CitizenId = 100, Handle = "alice", Rank = "Member" },
        };

        // Current snapshot: two entries for the same citizen_id, last one has a different rank.
        // The old code (g.First()) would have compared the "Member"/"Member" pair
        // and missed the rank change. The fixed code (g.Last()) correctly detects it.
        var current = new[]
        {
            new MemberSnapshot { CitizenId = 100, Handle = "alice", Rank = "Member" },
            new MemberSnapshot { CitizenId = 100, Handle = "alice", Rank = "Officer" },
        };

        var events = _sut.DetectMemberChanges("TESTORG", previous, current);

        events.Should().ContainSingle(e => e.ChangeType == "rank_changed");
        events.Single(e => e.ChangeType == "rank_changed").NewValue.Should().Be("Officer");
    }

    [Fact]
    public void DetectMemberChanges_DuplicateCitizenIdInPrevious_DoesNotMisdetectJoin()
    {
        // Two duplicates in previous (corrupted RSI roster), one normal entry in current.
        // The member IS still present — must not emit a "left" event.
        var previous = new[]
        {
            new MemberSnapshot { CitizenId = 100, Handle = "alice", Rank = "Member" },
            new MemberSnapshot { CitizenId = 100, Handle = "alice", Rank = "Officer" },
        };
        var current = new[]
        {
            new MemberSnapshot { CitizenId = 100, Handle = "alice", Rank = "Officer" },
        };

        var events = _sut.DetectMemberChanges("TESTORG", previous, current);

        events.Should().NotContain(e => e.ChangeType == "member_left");
        events.Should().NotContain(e => e.ChangeType == "member_joined");
    }
}
