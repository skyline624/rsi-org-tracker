using Collector.Dtos;
using Collector.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Collector.Tests.Services;

public class ChangeDetectorTests
{
    private readonly ChangeDetector _detector;

    public ChangeDetectorTests()
    {
        var logger = new Mock<ILogger<ChangeDetector>>();
        _detector = new ChangeDetector(logger.Object);
    }

    #region Member Change Detection Tests

    [Fact]
    public void DetectMemberChanges_NoChanges_ReturnsEmptyList()
    {
        // Arrange
        var previous = new List<MemberSnapshot>
        {
            new() { CitizenId = 123, Handle = "Alice", Rank = "Member" },
            new() { CitizenId = 456, Handle = "Bob", Rank = "Officer" }
        };

        var current = new List<MemberSnapshot>
        {
            new() { CitizenId = 123, Handle = "Alice", Rank = "Member" },
            new() { CitizenId = 456, Handle = "Bob", Rank = "Officer" }
        };

        // Act
        var changes = _detector.DetectMemberChanges("TEST", previous, current);

        // Assert
        changes.Should().BeEmpty();
    }

    [Fact]
    public void DetectMemberChanges_NewMember_DetectsJoin()
    {
        // Arrange
        var previous = new List<MemberSnapshot>
        {
            new() { CitizenId = 123, Handle = "Alice" }
        };

        var current = new List<MemberSnapshot>
        {
            new() { CitizenId = 123, Handle = "Alice" },
            new() { CitizenId = 456, Handle = "Bob" }
        };

        // Act
        var changes = _detector.DetectMemberChanges("TEST", previous, current);

        // Assert
        changes.Should().HaveCount(1);
        changes[0].ChangeType.Should().Be("member_joined");
        changes[0].UserHandle.Should().Be("Bob");
    }

    [Fact]
    public void DetectMemberChanges_MemberLeft_DetectsLeave()
    {
        // Arrange
        var previous = new List<MemberSnapshot>
        {
            new() { CitizenId = 123, Handle = "Alice" },
            new() { CitizenId = 456, Handle = "Bob" }
        };

        var current = new List<MemberSnapshot>
        {
            new() { CitizenId = 123, Handle = "Alice" }
        };

        // Act
        var changes = _detector.DetectMemberChanges("TEST", previous, current);

        // Assert
        changes.Should().HaveCount(1);
        changes[0].ChangeType.Should().Be("member_left");
        changes[0].UserHandle.Should().Be("Bob");
    }

    [Fact]
    public void DetectMemberChanges_HandleChangedButSameCitizenId_NoChange()
    {
        // Arrange - Same citizen_id but handle changed (handle reuse scenario)
        var previous = new List<MemberSnapshot>
        {
            new() { CitizenId = 123, Handle = "OldHandle" }
        };

        var current = new List<MemberSnapshot>
        {
            new() { CitizenId = 123, Handle = "NewHandle" }
        };

        // Act
        var changes = _detector.DetectMemberChanges("TEST", previous, current);

        // Assert
        // Should not detect as leave/join since citizen_id matches
        var joinOrLeave = changes.Where(c => c.ChangeType is "member_joined" or "member_left");
        joinOrLeave.Should().BeEmpty();
    }

    [Fact]
    public void DetectMemberChanges_HandleReuse_DetectsLeaveAndJoin()
    {
        // Arrange - Handle reused by different citizen
        var previous = new List<MemberSnapshot>
        {
            new() { CitizenId = 123, Handle = "TestHandle" }
        };

        var current = new List<MemberSnapshot>
        {
            new() { CitizenId = 456, Handle = "TestHandle" } // Same handle, different citizen
        };

        // Act
        var changes = _detector.DetectMemberChanges("TEST", previous, current);

        // Assert
        // Should detect the original as left (different citizen_id for same handle)
        // and the new one as joined (different citizen_id)
        var leaves = changes.Where(c => c.ChangeType == "member_left");
        var joins = changes.Where(c => c.ChangeType == "member_joined");

        leaves.Should().HaveCount(1);
        joins.Should().HaveCount(1);
    }

    [Fact]
    public void DetectMemberChanges_RankChanged_DetectsRankChange()
    {
        // Arrange
        var previous = new List<MemberSnapshot>
        {
            new() { CitizenId = 123, Handle = "Alice", Rank = "Member" }
        };

        var current = new List<MemberSnapshot>
        {
            new() { CitizenId = 123, Handle = "Alice", Rank = "Officer" }
        };

        // Act
        var changes = _detector.DetectMemberChanges("TEST", previous, current);

        // Assert
        changes.Should().HaveCount(1);
        changes[0].ChangeType.Should().Be("rank_changed");
        changes[0].OldValue.Should().Be("Member");
        changes[0].NewValue.Should().Be("Officer");
    }

    [Fact]
    public void DetectMemberChanges_MemberWithNullCitizenId_TrackedByHandle()
    {
        // Arrange - Members without citizen_id (fallback to handle)
        var previous = new List<MemberSnapshot>
        {
            new() { CitizenId = null, Handle = "Unknown" }
        };

        var current = new List<MemberSnapshot>
        {
            new() { CitizenId = null, Handle = "Unknown" }
        };

        // Act
        var changes = _detector.DetectMemberChanges("TEST", previous, current);

        // Assert
        changes.Should().BeEmpty(); // Same handle = same member
    }

    #endregion

    #region Organization Change Detection Tests

    [Fact]
    public void DetectOrganizationChanges_FirstSnapshot_ReturnsEmptyList()
    {
        // Arrange
        OrganizationSnapshot? previous = null;
        var current = new OrganizationSnapshot
        {
            Sid = "TEST",
            Name = "Test Org",
            Members = 100
        };

        // Act
        var changes = _detector.DetectOrganizationChanges(previous, current, "TEST");

        // Assert
        changes.Should().BeEmpty();
    }

    [Fact]
    public void DetectOrganizationChanges_NameChanged_DetectsChange()
    {
        // Arrange
        var previous = new OrganizationSnapshot
        {
            Sid = "TEST",
            Name = "Old Name",
            Members = 100
        };

        var current = new OrganizationSnapshot
        {
            Sid = "TEST",
            Name = "New Name",
            Members = 100
        };

        // Act
        var changes = _detector.DetectOrganizationChanges(previous, current, "TEST");

        // Assert
        changes.Should().HaveCount(1);
        changes[0].ChangeType.Should().Be("name_changed");
        changes[0].OldValue.Should().Be("Old Name");
        changes[0].NewValue.Should().Be("New Name");
    }

    [Fact]
    public void DetectOrganizationChanges_MemberCountChanged_DetectsChange()
    {
        // Arrange
        var previous = new OrganizationSnapshot
        {
            Sid = "TEST",
            Name = "Test Org",
            Members = 100
        };

        var current = new OrganizationSnapshot
        {
            Sid = "TEST",
            Name = "Test Org",
            Members = 105
        };

        // Act
        var changes = _detector.DetectOrganizationChanges(previous, current, "TEST");

        // Assert
        changes.Should().HaveCount(1);
        changes[0].ChangeType.Should().Be("member_count_changed");
        changes[0].OldValue.Should().Be("100");
        changes[0].NewValue.Should().Be("105");
    }

    #endregion
}