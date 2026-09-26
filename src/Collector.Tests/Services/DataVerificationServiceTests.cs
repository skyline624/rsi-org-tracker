using Collector.Data;
using Collector.Extensions;
using Collector.Models;
using Collector.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Collector.Tests.Services;

/// <summary>
/// <c>--maintenance verify</c>: each check finds the incoherence it describes, and a
/// consistent organization passes them all.
/// </summary>
public sealed class DataVerificationServiceTests : IAsyncLifetime
{
    private static readonly DateTime Read = new(2026, 9, 26, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Since = Read.AddHours(-1);
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync()
    {
        _connection.Open();
        _provider = new ServiceCollection()
            .AddDbContext<TrackerDbContext>(o => o.UseSqlite(_connection))
            .BuildServiceProvider();
        await _provider.EnsureDatabaseAsync(Path.GetTempPath());
        await SeedConsistentOrgAsync();
    }

    private TrackerDbContext NewDb() => _provider.CreateScope().ServiceProvider.GetRequiredService<TrackerDbContext>();

    /// <summary>ORG, read at <see cref="Read"/>: members alpha and bravo, everything agreeing.</summary>
    private async Task SeedConsistentOrgAsync()
    {
        var db = NewDb();
        db.DiscoveredOrganizations.Add(new DiscoveredOrganization
        {
            Sid = "ORG", Name = "Org", DiscoveredAt = Read.AddDays(-30), LastMembersCollectedAt = Read, ContentCheckedAt = Read,
        });
        db.Organizations.Add(new Organization { Sid = "ORG", Name = "Org", Timestamp = Read.AddDays(-2), MembersCount = 2 });
        db.OrgMemberCounts.Add(new OrgMemberCount
        {
            OrgSid = "ORG", CollectedAt = Read, TotalRows = 2, VisibleCount = 2, RedactedCount = 0, HiddenCount = 0,
        });
        foreach (var handle in new[] { "alpha", "bravo" })
        {
            db.MemberCollectionLogs.Add(new MemberCollectionLog
            {
                OrgSid = "ORG", CollectionTime = Read, UserHandle = handle, Rank = "Pilot", ParserVersion = 2,
            });
            db.OrganizationMembers.Add(new OrganizationMember
            {
                OrgSid = "ORG", UserHandle = handle, Rank = "Pilot", Stars = 3, Timestamp = Read, IsActive = true,
            });
        }
        await db.SaveChangesAsync();
    }

    private async Task<IReadOnlyList<VerificationResult>> VerifyAsync()
        => await new DataVerificationService(NewDb(), NullLogger<DataVerificationService>.Instance).VerifyAsync(Since);

    private async Task<long> CountAsync(string check) => (await VerifyAsync()).Single(r => r.Name == check).Count;

    private async Task ChangeAsync(Action<TrackerDbContext> change)
    {
        var db = NewDb();
        change(db);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task AConsistentOrganization_PassesEveryCheck()
    {
        var results = await VerifyAsync();

        results.Select(r => r.Name).Should().Equal(DataVerificationService.CheckNames);
        results.Where(r => r.Count > 0).Select(r => $"{r.Name}: {string.Join("; ", r.Samples)}").Should().BeEmpty();
    }

    [Fact]
    public async Task AMemberActiveTwice_IsFound()
    {
        await ChangeAsync(db => db.OrganizationMembers.Add(new OrganizationMember
        {
            OrgSid = "ORG", UserHandle = "ALPHA", Timestamp = Read, IsActive = true,
        }));

        (await CountAsync("duplicate-active-members")).Should().Be(1);
    }

    [Fact]
    public async Task AMemberReadButInactive_IsFound()
    {
        await ChangeAsync(db => db.OrganizationMembers.Single(m => m.UserHandle == "bravo").IsActive = false);

        (await CountAsync("read-but-not-active")).Should().Be(1);
        (await CountAsync("active-count-differs-from-visible")).Should().Be(1);
    }

    [Fact]
    public async Task AnActiveMemberMissingFromTheLastRead_IsFound()
    {
        await ChangeAsync(db => db.OrganizationMembers.Add(new OrganizationMember
        {
            OrgSid = "ORG", UserHandle = "ghost", Timestamp = Read.AddDays(-3), IsActive = true,
        }));

        (await CountAsync("active-but-not-read")).Should().Be(1);
    }

    [Fact]
    public async Task AnEmptiedRoster_IsReported()
    {
        await ChangeAsync(db => { foreach (var m in db.OrganizationMembers) m.IsActive = false; });

        var results = await VerifyAsync();

        results.Single(r => r.Name == "emptied-rosters").Count.Should().Be(1);
        results.Single(r => r.Name == "read-but-not-active").Count.Should().Be(0, "emptied rosters are reported apart");
    }

    [Fact]
    public async Task AMembersCountThatIsNotRsisTotal_IsFound()
    {
        await ChangeAsync(db => db.Organizations.Single().MembersCount = 5);

        (await CountAsync("members-count-differs-from-rsi")).Should().Be(1);
    }

    [Fact]
    public async Task ARankNamedAffiliate_IsARankAnOrgChose()
    {
        await ChangeAsync(db => db.MemberCollectionLogs.First().Rank = "Affiliate");

        (await CountAsync("overlay-titles-as-ranks")).Should().Be(0);
    }

    [Fact]
    public async Task AReadWhereEveryRankIsAnOverlayTitle_IsFound()
    {
        // The v1 parser's fault: the "Roles" / "Affiliate" overlay read as everyone's rank.
        await ChangeAsync(db =>
        {
            foreach (var log in db.MemberCollectionLogs) log.Rank = "Roles";
            foreach (var handle in new[] { "charlie", "delta", "echo" })
            {
                db.MemberCollectionLogs.Add(new MemberCollectionLog
                {
                    OrgSid = "ORG", CollectionTime = Read, UserHandle = handle, Rank = "Affiliate", ParserVersion = 2,
                });
            }
        });

        (await CountAsync("overlay-titles-as-ranks")).Should().Be(1);
    }

    [Fact]
    public async Task StarsOutOfRange_AreFound()
    {
        await ChangeAsync(db => db.OrganizationMembers.First().Stars = 7);

        (await CountAsync("stars-out-of-range")).Should().Be(1);
    }

    [Fact]
    public async Task ALeaverStillActive_IsFound()
    {
        await ChangeAsync(db => db.ChangeEvents.Add(new ChangeEvent
        {
            Timestamp = Read.AddSeconds(1), EntityType = "member", EntityId = "alpha", ChangeType = "member_left",
            OrgSid = "ORG", UserHandle = "alpha",
        }));

        (await CountAsync("left-but-still-active")).Should().Be(1);
    }

    [Fact]
    public async Task AJoinOfSomeoneNeverInTheRoster_IsFound()
    {
        await ChangeAsync(db => db.ChangeEvents.Add(new ChangeEvent
        {
            Timestamp = Read, EntityType = "member", EntityId = "stranger", ChangeType = "member_joined",
            OrgSid = "ORG", UserHandle = "stranger",
        }));

        (await CountAsync("joined-but-never-in-roster")).Should().Be(1);
    }

    [Fact]
    public async Task TheSameEventTwice_IsFound()
    {
        await ChangeAsync(db =>
        {
            for (var i = 0; i < 2; i++)
            {
                db.ChangeEvents.Add(new ChangeEvent
                {
                    Timestamp = Read, EntityType = "organization", EntityId = "ORG", ChangeType = "name_changed",
                    OrgSid = "ORG", OldValue = "Old", NewValue = "Org",
                });
            }
        });

        (await CountAsync("duplicate-events")).Should().Be(1);
    }

    [Fact]
    public async Task AFirstDisplayNameRecordedAsAChange_IsFound()
    {
        await ChangeAsync(db => db.ChangeEvents.Add(new ChangeEvent
        {
            Timestamp = Read, EntityType = "user", EntityId = "100001", ChangeType = "display_name_changed", NewValue = "Pilot",
        }));

        (await CountAsync("citizen-field-first-value-as-change")).Should().Be(1);
    }

    [Fact]
    public async Task ARenameToTheSameHandle_IsFound()
    {
        await ChangeAsync(db => db.ChangeEvents.Add(new ChangeEvent
        {
            Timestamp = Read, EntityType = "member", EntityId = "alpha", ChangeType = "handle_changed", OldValue = "alpha", NewValue = "alpha",
        }));

        (await CountAsync("rename-to-same-handle")).Should().Be(1);
    }

    [Fact]
    public async Task AnUnchangedListingSnapshot_IsFound()
    {
        await ChangeAsync(db => db.Organizations.Add(new Organization { Sid = "ORG", Name = "Org", Timestamp = Read, MembersCount = 2 }));

        (await CountAsync("unchanged-listing-snapshot")).Should().Be(1);
    }

    [Fact]
    public async Task AnUnchangedContentSnapshot_IsFound()
    {
        await ChangeAsync(db =>
        {
            db.Organizations.Add(new Organization
            {
                Sid = "ORG", Name = "Org", Timestamp = Read.AddDays(-1), MembersCount = 2, ContentCollected = true, Description = "About us",
            });
            db.Organizations.Add(new Organization
            {
                Sid = "ORG", Name = "Org", Timestamp = Read, MembersCount = 2, ContentCollected = true, Description = "About us",
            });
        });

        (await CountAsync("unchanged-content-snapshot")).Should().Be(1);
    }

    [Fact]
    public async Task ANameStoredWithHtmlEntities_IsFound()
    {
        await ChangeAsync(db => db.Organizations.Add(new Organization { Sid = "ORG", Name = "Org &amp; Co", Timestamp = Read, MembersCount = 2 }));

        (await CountAsync("org-name-html-entities")).Should().Be(1);
    }

    [Fact]
    public async Task ANameWithAnAmpersandAndASemicolon_IsNotAnEncodedName()
    {
        await ChangeAsync(db => db.Organizations.Add(new Organization { Sid = "ORG", Name = "Salt & Pepper; Co", Timestamp = Read, MembersCount = 2 }));

        (await CountAsync("org-name-html-entities")).Should().Be(0);
    }

    [Fact]
    public async Task ARosterRowMatchesItsCitizen_RegardlessOfTheHandlesCase()
    {
        // Phase 3 maps handles to citizens regardless of case (the newest row wins).
        await ChangeAsync(db =>
        {
            db.Users.Add(new User { CitizenId = 10, UserHandle = "alpha", CreatedAt = Read, UpdatedAt = Read.AddDays(-90), DisplayName = "A", Enlisted = Read });
            db.Users.Add(new User { CitizenId = 11, UserHandle = "ALPHA", CreatedAt = Read, UpdatedAt = Read.AddDays(-9), DisplayName = "B", Enlisted = Read });
            db.OrganizationMembers.Single(m => m.UserHandle == "alpha").CitizenId = 11;
        });

        (await CountAsync("roster-and-citizen-disagree")).Should().Be(0);
    }

    [Fact]
    public async Task ADeadOrganizationReadAgain_IsFound()
    {
        await ChangeAsync(db => db.DiscoveredOrganizations.Single().DeadAt = Read.AddMinutes(-5));

        (await CountAsync("dead-org-read")).Should().Be(1);
    }

    [Fact]
    public async Task ARosterRowOfAnotherCitizen_IsFound()
    {
        await ChangeAsync(db =>
        {
            db.Users.Add(new User { CitizenId = 1, UserHandle = "alpha", CreatedAt = Read, UpdatedAt = Read.AddDays(-9), DisplayName = "A", Enlisted = Read });
            db.OrganizationMembers.Single(m => m.UserHandle == "alpha").CitizenId = 2;
        });

        (await CountAsync("roster-and-citizen-disagree")).Should().Be(1);
    }

    [Fact]
    public async Task AReusedHandle_IsReported_AndTheRosterMayMatchEitherHolder()
    {
        await ChangeAsync(db =>
        {
            db.Users.Add(new User { CitizenId = 1, UserHandle = "alpha", CreatedAt = Read, UpdatedAt = Read.AddDays(-90), DisplayName = "A", Enlisted = Read });
            db.Users.Add(new User { CitizenId = 2, UserHandle = "alpha", CreatedAt = Read, UpdatedAt = Read.AddDays(-9), DisplayName = "B", Enlisted = Read });
            db.OrganizationMembers.Single(m => m.UserHandle == "alpha").CitizenId = 2;
        });

        (await CountAsync("roster-and-citizen-disagree")).Should().Be(0);
        (await CountAsync("handle-held-by-two-citizens")).Should().Be(1);
    }

    [Fact]
    public async Task ACitizenUpdatedWithoutDisplayName_OrWithABadLocation_IsFound()
    {
        await ChangeAsync(db => db.Users.Add(new User
        {
            CitizenId = 3, UserHandle = "charlie", CreatedAt = Read, UpdatedAt = Read, Enlisted = Read, Location = "United States , Texas",
        }));

        (await CountAsync("citizen-profile-incomplete")).Should().Be(1);
        (await CountAsync("location-format")).Should().Be(1);
    }

    [Fact]
    public async Task AQueueRowWhoseFlagContradictsItsOutcome_IsFound()
    {
        await ChangeAsync(db => db.UserEnrichmentQueue.Add(new UserEnrichmentQueue
        {
            UserHandle = "delta", Enriched = false, Outcome = EnrichmentOutcome.Gone, QueuedAt = Read,
        }));

        (await CountAsync("queue-state")).Should().Be(1);
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        _connection.Dispose();
    }
}
