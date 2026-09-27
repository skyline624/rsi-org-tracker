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

/// <summary>
/// Phase 4 reading again the profiles stored by an older parser: on the copy of
/// 2026-09-24, 62 % of the citizens had no display name and 84 % no enlistment date,
/// and a known citizen is otherwise never read again.
/// </summary>
public sealed class ProfileRefreshTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly Mock<IRsiApiClient> _rsi = new();
    private readonly List<string> _fetched = [];
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

    /// <summary>The fixture profile, as RSI would answer it for another citizen.</summary>
    private static string ProfileOf(string handle, int citizenId) => RsiFixtures.Text("profile-citizen.html")
        .Replace("fixture-pilot", handle)
        .Replace("#100001", $"#{citizenId}");

    private void Answer(string handle, UserProfileFetchOutcome outcome, string? html = null)
        => _rsi.Setup(r => r.GetUserProfileResultAsync(handle, It.IsAny<CancellationToken>()))
            .Callback(() => { lock (_fetched) _fetched.Add(handle); })
            .ReturnsAsync(new UserProfileFetchResult(html, outcome));

    private async Task<User> SeedAsync(string handle, int citizenId, int parserVersion = 1, string? displayName = null)
    {
        var user = new User
        {
            CitizenId = citizenId, UserHandle = handle, DisplayName = displayName, Location = "Belgium",
            ParserVersion = parserVersion,
            CreatedAt = DateTime.UtcNow.AddMonths(-10), UpdatedAt = DateTime.UtcNow.AddMonths(-10),
        };
        var db = NewDb();
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private Task<ProfileRefreshResult> RefreshAsync(long afterId = 0)
    {
        var db = NewDb();
        return new UserCollector(
            _rsi.Object,
            new UserRepository(db),
            new UserHandleHistoryRepository(db),
            new UserEnrichmentQueueRepository(db),
            new OrganizationMemberRepository(db),
            new ChangeEventRepository(db),
            new UserChangeDetector(NullLogger<UserChangeDetector>.Instance),
            new UserProfileHtmlParser(NullLogger<UserProfileHtmlParser>.Instance),
            NullLogger<UserCollector>.Instance,
            Microsoft.Extensions.Options.Options.Create(new CollectorOptions())).RefreshProfilesAsync(afterId);
    }

    private async Task<User> StoredAsync(long id) => await NewDb().Users.AsNoTracking().SingleAsync(u => u.Id == id);

    [Fact]
    public async Task AProfileReadByTheOldParser_IsReadAgain_AndStoredWithoutEvents()
    {
        var user = await SeedAsync("fixture-pilot", 100001, displayName: "Old Name");
        Answer("fixture-pilot", UserProfileFetchOutcome.Ok, ProfileOf("fixture-pilot", 100001));

        var result = await RefreshAsync();

        result.Should().Be(new ProfileRefreshResult(user.Id, Refreshed: 1, Gone: 0, NoCitizenRecord: 0, TakenOver: 0, Failed: 0));
        var stored = await StoredAsync(user.Id);
        stored.DisplayName.Should().Be("fixture-pilot");
        stored.Enlisted.Should().Be(new DateTime(2020, 1, 1));
        stored.ParserVersion.Should().Be(UserProfileHtmlParser.Version);
        (await NewDb().ChangeEvents.AsNoTracking().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task AProfileAlreadyReadByTheCurrentParser_IsNotFetched()
    {
        await SeedAsync("fixture-pilot", 100001, parserVersion: UserProfileHtmlParser.Version);

        (await RefreshAsync()).Processed.Should().Be(0);
        _fetched.Should().BeEmpty();
    }

    [Fact]
    public async Task ABatch_ContinuesAfterTheCursor_InIdOrder()
    {
        var first = await SeedAsync("pilot-one", 101);
        var second = await SeedAsync("pilot-two", 102);
        var third = await SeedAsync("pilot-three", 103);
        foreach (var (handle, id) in new[] { ("pilot-one", 101), ("pilot-two", 102), ("pilot-three", 103) })
        {
            Answer(handle, UserProfileFetchOutcome.Ok, ProfileOf(handle, id));
        }

        var result = await RefreshAsync(afterId: first.Id);

        result.LastId.Should().Be(third.Id);
        result.Refreshed.Should().Be(2);
        _fetched.Should().BeEquivalentTo("pilot-two", "pilot-three");
        (await StoredAsync(first.Id)).ParserVersion.Should().Be(1);
        (await StoredAsync(second.Id)).ParserVersion.Should().Be(UserProfileHtmlParser.Version);
    }

    [Fact]
    public async Task AHandleThatAnswers404_IsNotReadAgain_AndKeepsWhatWasStored()
    {
        var user = await SeedAsync("fixture-pilot", 100001, displayName: "Old Name");
        Answer("fixture-pilot", UserProfileFetchOutcome.NotFound);

        (await RefreshAsync()).Gone.Should().Be(1);

        var stored = await StoredAsync(user.Id);
        stored.ParserVersion.Should().Be(UserProfileHtmlParser.Version);
        stored.DisplayName.Should().Be("Old Name");
    }

    [Fact]
    public async Task AProfileWithoutCitizenRecord_IsNotReadAgain()
    {
        var user = await SeedAsync("fixture-pilot", 100001);
        Answer("fixture-pilot", UserProfileFetchOutcome.Ok, RsiFixtures.Text("profile-no-citizen-record.html"));

        (await RefreshAsync()).NoCitizenRecord.Should().Be(1);

        (await StoredAsync(user.Id)).ParserVersion.Should().Be(UserProfileHtmlParser.Version);
    }

    [Fact]
    public async Task AHandleNowHeldByAnotherCitizen_LeavesTheRowAsItIs_AndIsNotReadAgain()
    {
        // Citizen 200 gave "fixture-pilot" up and 100001 took it. Nothing says where 200
        // went: their row is fixed when their new handle shows up in a roster.
        var user = await SeedAsync("fixture-pilot", 200, displayName: "Former Owner");
        Answer("fixture-pilot", UserProfileFetchOutcome.Ok, ProfileOf("fixture-pilot", 100001));

        (await RefreshAsync()).TakenOver.Should().Be(1);

        var stored = await StoredAsync(user.Id);
        (stored.CitizenId, stored.DisplayName, stored.ParserVersion).Should().Be((200, "Former Owner", UserProfileHtmlParser.Version));
        (await NewDb().Users.CountAsync()).Should().Be(1);
        (await NewDb().ChangeEvents.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData(UserProfileFetchOutcome.Failed, null)]
    [InlineData(UserProfileFetchOutcome.Ok, "<html><body>Maintenance</body></html>")]
    public async Task AFailedRead_IsLeftForTheNextPass(UserProfileFetchOutcome outcome, string? html)
    {
        var user = await SeedAsync("fixture-pilot", 100001);
        Answer("fixture-pilot", outcome, html);

        var result = await RefreshAsync();

        (result.Failed, result.LastId).Should().Be((1, user.Id));
        (await StoredAsync(user.Id)).ParserVersion.Should().Be(1);
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        _connection.Dispose();
    }
}
