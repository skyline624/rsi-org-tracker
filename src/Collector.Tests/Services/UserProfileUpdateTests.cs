using Collector.Data;
using Collector.Data.Repositories;
using Collector.Dtos;
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

/// <summary>
/// Phase 4 updating a known citizen from their profile. Most stored users have no
/// display name or location (the parser could not read them): the first value read
/// fills the field silently; only a change of a known value is an event.
/// </summary>
public sealed class UserProfileUpdateTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly UserChangeDetector _detector = new(NullLogger<UserChangeDetector>.Instance);
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

    private static User Stored(string? displayName = null, string? location = null) => new()
    {
        CitizenId = 100001, UserHandle = "fixture-pilot", DisplayName = displayName, Location = location,
        CreatedAt = DateTime.UtcNow.AddDays(-30), UpdatedAt = DateTime.UtcNow.AddDays(-30),
    };

    private static UserProfileData Read(string? displayName, string? location = null) => new()
    {
        CitizenId = 100001, Handle = "fixture-pilot", DisplayName = displayName, Location = location,
    };

    [Fact]
    public void AFirstKnownDisplayNameOrLocation_IsNotAChange()
    {
        _detector.DetectUserChanges(Stored(), Read("Fixture Pilot", "France, Paris")).Should().BeEmpty();
    }

    [Fact]
    public void AChangeOfAKnownDisplayNameOrLocation_IsAnEvent()
    {
        var events = _detector.DetectUserChanges(Stored("Old Name", "Belgium"), Read("New Name", "France, Paris"));

        events.Select(e => (e.ChangeType, e.OldValue, e.NewValue)).Should().BeEquivalentTo(new[]
        {
            ("display_name_changed", "Old Name", "New Name"),
            ("location_changed", "Belgium", "France, Paris"),
        });
    }

    [Fact]
    public async Task EnrichingAKnownCitizen_FillsTheProfileFields()
    {
        await SeedAsync(Stored());

        await EnrichAsync(RsiFixtures.Text("profile-citizen.html"));

        var user = await NewDb().Users.AsNoTracking().SingleAsync(u => u.CitizenId == 100001);
        user.DisplayName.Should().Be("fixture-pilot");
        user.Enlisted.Should().Be(new DateTime(2020, 1, 1));
        user.Bio.Should().Be("Fixture bio.");
        (await NewDb().ChangeEvents.AsNoTracking().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ADisplayNameThatCannotBeRead_DoesNotEraseTheKnownOne()
    {
        await SeedAsync(Stored("Known Name"));
        var html = RsiFixtures.Text("profile-citizen.html");
        const string firstValue = "<strong class=\"value\">fixture-pilot</strong>";
        var i = html.IndexOf(firstValue, StringComparison.Ordinal);
        html = html[..i] + "<strong class=\"value\"> </strong>" + html[(i + firstValue.Length)..];

        await EnrichAsync(html);

        (await NewDb().Users.AsNoTracking().SingleAsync(u => u.CitizenId == 100001)).DisplayName.Should().Be("Known Name");
    }

    private async Task SeedAsync(User user)
    {
        var db = NewDb();
        db.Users.Add(user);
        await db.SaveChangesAsync();
    }

    private async Task EnrichAsync(string html)
    {
        var db = NewDb();
        var collector = new UserCollector(
            Mock.Of<IRsiApiClient>(),
            new UserRepository(db),
            new UserHandleHistoryRepository(db),
            new UserEnrichmentQueueRepository(db),
            new OrganizationMemberRepository(db),
            new ChangeEventRepository(db),
            _detector,
            new UserProfileHtmlParser(NullLogger<UserProfileHtmlParser>.Instance),
            NullLogger<UserCollector>.Instance,
            Microsoft.Extensions.Options.Options.Create(new CollectorOptions()));
        (await collector.EnrichUserAsync("fixture-pilot", isNewHandle: false, html)).Should().BeTrue();
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        _connection.Dispose();
    }
}
