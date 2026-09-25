using Collector.Data;
using Collector.Data.Repositories;
using Collector.Extensions;
using Collector.Models;
using Collector.Options;
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

/// <summary>Phase 4 announces a newly identified citizen in the orgs they are in now.</summary>
public sealed class UserCollectorJoinTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync()
    {
        _connection.Open();
        _provider = new ServiceCollection()
            .AddDbContext<TrackerDbContext>(o => o.UseSqlite(_connection))
            .BuildServiceProvider();
        await _provider.EnsureDatabaseAsync(Path.GetTempPath());
    }

    private TrackerDbContext NewDb() => _provider.CreateScope().ServiceProvider.GetRequiredService<TrackerDbContext>();

    [Fact]
    public async Task ANewCitizen_IsAnnouncedOnlyInOrgsTheyAreActiveIn()
    {
        var seed = NewDb();
        seed.OrganizationMembers.AddRange(
            new OrganizationMember { OrgSid = "NOW", UserHandle = "fixture-pilot", Timestamp = DateTime.UtcNow, IsActive = true },
            new OrganizationMember { OrgSid = "BEFORE", UserHandle = "fixture-pilot", Timestamp = DateTime.UtcNow.AddYears(-1), IsActive = false });
        await seed.SaveChangesAsync();

        var db = NewDb();
        var collector = new UserCollector(
            Mock.Of<IRsiApiClient>(),
            new UserRepository(db),
            new UserHandleHistoryRepository(db),
            new UserEnrichmentQueueRepository(db),
            new OrganizationMemberRepository(db),
            new ChangeEventRepository(db),
            new UserChangeDetector(NullLogger<UserChangeDetector>.Instance),
            new UserProfileHtmlParser(NullLogger<UserProfileHtmlParser>.Instance),
            NullLogger<UserCollector>.Instance,
            Microsoft.Extensions.Options.Options.Create(new CollectorOptions()));

        (await collector.EnrichUserAsync("fixture-pilot", isNewHandle: true, RsiFixtures.Text("profile-citizen.html")))
            .Should().BeTrue();

        (await NewDb().ChangeEvents.AsNoTracking().Where(e => e.ChangeType == "member_joined").Select(e => e.OrgSid).ToListAsync())
            .Should().Equal("NOW");
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        _connection.Dispose();
    }
}
