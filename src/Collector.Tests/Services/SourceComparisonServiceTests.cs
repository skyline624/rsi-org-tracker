using Collector.Data;
using Collector.Dtos;
using Collector.Extensions;
using Collector.Models;
using Collector.Parsers;
using Collector.Services;
using Collector.Tests.TestSupport;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Collector.Tests.Services;

/// <summary>
/// <c>--integrity-check</c>: recently read rosters and profiles compared with what RSI
/// serves now.
/// </summary>
public sealed class SourceComparisonServiceTests : IAsyncLifetime
{
    private static readonly DateTime Read = DateTime.UtcNow.AddHours(-2);
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly Mock<IRsiApiClient> _rsi = new();
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync()
    {
        _connection.Open();
        _provider = new ServiceCollection()
            .AddDbContext<TrackerDbContext>(o => o.UseSqlite(_connection))
            .BuildServiceProvider();
        await _provider.EnsureDatabaseAsync(Path.GetTempPath());

        var db = NewDb();
        db.DiscoveredOrganizations.Add(new DiscoveredOrganization
        {
            Sid = "ORG", Name = "Org", DiscoveredAt = Read.AddDays(-9), LastMembersCollectedAt = Read,
        });
        db.OrgMemberCounts.Add(new OrgMemberCount
        {
            OrgSid = "ORG", CollectedAt = Read, TotalRows = 3, VisibleCount = 2, RedactedCount = 1, HiddenCount = 0,
        });
        db.OrganizationMembers.AddRange(
            new OrganizationMember { OrgSid = "ORG", UserHandle = "alpha", Rank = "Pilot", Timestamp = Read, IsActive = true },
            new OrganizationMember { OrgSid = "ORG", UserHandle = "bravo", Rank = "Pilot", Timestamp = Read, IsActive = true },
            new OrganizationMember { OrgSid = "ORG", UserHandle = "gone", Rank = "Pilot", Timestamp = Read.AddDays(-3), IsActive = false });
        db.Users.Add(new User
        {
            CitizenId = 100001, UserHandle = "fixture-pilot", DisplayName = "fixture-pilot", Bio = "Fixture bio.",
            Enlisted = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            UrlImage = "https://robertsspaceindustries.com/media/fixture/avatar.jpg",
            CreatedAt = Read.AddDays(-9), UpdatedAt = Read,
        });
        await db.SaveChangesAsync();
    }

    private TrackerDbContext NewDb() => _provider.CreateScope().ServiceProvider.GetRequiredService<TrackerDbContext>();

    private SourceComparisonService Create() => new(
        NewDb(), _rsi.Object, new UserProfileHtmlParser(NullLogger<UserProfileHtmlParser>.Instance),
        NullLogger<SourceComparisonService>.Instance);

    private void LiveRoster(RosterStatus status, int totalRows, params (string Handle, string Rank)[] members)
        => _rsi.Setup(r => r.GetAllOrganizationMembersAsync("ORG", It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemberCollectionResult(status,
                members.Select(m => new MemberData { OrgSid = "ORG", Handle = m.Handle, Rank = m.Rank }).ToList(),
                totalRows, totalRows, 0, totalRows - members.Length));

    [Fact]
    public async Task ARosterStillAsStored_HasNoDifference()
    {
        LiveRoster(RosterStatus.Complete, 3, ("ALPHA", "Pilot"), ("bravo", "Pilot"));

        var result = (await Create().CompareRostersAsync(5, Read.AddHours(-1))).Single();

        result.Sid.Should().Be("ORG");
        result.Skipped.Should().BeNull();
        result.IsSame.Should().BeTrue();
    }

    [Fact]
    public async Task ARosterThatDiffers_ListsWhoAndWhat()
    {
        LiveRoster(RosterStatus.Complete, 4, ("alpha", "Captain"), ("charlie", "Pilot"));

        var result = (await Create().CompareRostersAsync(5, Read.AddHours(-1))).Single();

        result.OnlyStored.Should().Equal("bravo");
        result.OnlyLive.Should().Equal("charlie");
        result.RankDiffers.Should().Equal("alpha: Pilot → Captain");
        (result.StoredTotal, result.LiveTotal).Should().Be((3, 4));
        result.IsSame.Should().BeFalse();
    }

    [Fact]
    public async Task ActiveRowsDifferingOnlyByCase_DoNotAbortTheCheck()
    {
        var db = NewDb();
        db.OrganizationMembers.Add(new OrganizationMember { OrgSid = "ORG", UserHandle = "ALPHA", Rank = "Pilot", Timestamp = Read, IsActive = true });
        await db.SaveChangesAsync();
        LiveRoster(RosterStatus.Complete, 3, ("alpha", "Pilot"), ("bravo", "Pilot"));

        var act = () => Create().CompareRostersAsync(5, Read.AddHours(-1));

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task AReusedHandle_IsComparedWithTheCitizenThatWasSampled()
    {
        // The former owner of the handle, stored first: it must not be the row compared.
        var db = NewDb();
        var current = await db.Users.SingleAsync(u => u.CitizenId == 100001);
        db.Users.Remove(current);
        await db.SaveChangesAsync();
        db.Users.Add(new User { CitizenId = 1, UserHandle = "fixture-pilot", DisplayName = "Former", CreatedAt = Read.AddYears(-3), UpdatedAt = Read.AddDays(-400) });
        await db.SaveChangesAsync();
        db.Users.Add(new User
        {
            CitizenId = 100001, UserHandle = "fixture-pilot", DisplayName = "fixture-pilot", Bio = "Fixture bio.",
            Enlisted = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            UrlImage = "https://robertsspaceindustries.com/media/fixture/avatar.jpg", CreatedAt = Read, UpdatedAt = Read,
        });
        await db.SaveChangesAsync();
        _rsi.Setup(r => r.GetUserProfileResultAsync("fixture-pilot", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserProfileFetchResult(RsiFixtures.Text("profile-citizen.html"), UserProfileFetchOutcome.Ok));

        var result = (await Create().CompareProfilesAsync(5, Read.AddHours(-1))).Single();

        result.Differences.Select(d => d.Field).Should().BeEmpty();
    }

    [Fact]
    public async Task AnIncompleteLiveRead_IsSkipped_NotCompared()
    {
        LiveRoster(RosterStatus.Partial, 3, ("alpha", "Pilot"));

        (await Create().CompareRostersAsync(5, Read.AddHours(-1))).Single().Skipped.Should().Be("live read Partial");
    }

    [Fact]
    public async Task AProfileStillAsStored_HasNoDifference()
    {
        _rsi.Setup(r => r.GetUserProfileResultAsync("fixture-pilot", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserProfileFetchResult(RsiFixtures.Text("profile-citizen.html"), UserProfileFetchOutcome.Ok));

        var result = (await Create().CompareProfilesAsync(5, Read.AddHours(-1))).Single();

        result.Differences.Select(d => $"{d.Field}: {d.DbValue} → {d.LiveValue}").Should().BeEmpty();
    }

    [Fact]
    public async Task AProfileThatDiffers_ListsTheFields()
    {
        _rsi.Setup(r => r.GetUserProfileResultAsync("fixture-pilot", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserProfileFetchResult(
                RsiFixtures.Text("profile-citizen.html").Replace("Fixture bio.", "New bio."), UserProfileFetchOutcome.Ok));

        var result = (await Create().CompareProfilesAsync(5, Read.AddHours(-1))).Single();

        result.Differences.Select(d => (d.Field, d.DbValue, d.LiveValue)).Should().Equal(("Bio", "Fixture bio.", "New bio."));
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        _connection.Dispose();
    }
}
