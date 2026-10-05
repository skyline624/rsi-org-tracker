using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Collector.Api.Options;
using Collector.Api.Services.Discord;
using Collector.Models;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using static Collector.Api.Tests.Discord.DiscordTestKit;

namespace Collector.Api.Tests.Discord;

[Collection(ApiCollection.Name)]
public sealed class DiscordAutoLinkTests(ApiFactory factory)
{
    private readonly DiscordReadSeed _seed = new(factory);
    private static int _sequence = 69_000;
    private static int Next() => Interlocked.Increment(ref _sequence);
    private static int Cid(int n, int k = 0) => 1_300_000_000 + n * 10 + k;

    [Fact]
    public async Task StrongLinksAreSavedOnce_WithTheCurrentIdentity_AndMediumMatchesRemainManual()
    {
        var n = Next();
        var sid = $"AL{n}";
        await _seed.SeedRosterAsync(sid, $"Former{n}", Cid(n), null);
        await _seed.SeedRosterAsync(sid, $"Medium{n}", Cid(n, 1), null, active: false);
        await SeedUserAsync(Cid(n), $"Current{n}");
        await SeedUserAsync(Cid(n, 1), $"Medium{n}");
        var guild = await _seed.SeedGuildAsync(sid);
        var strong = await _seed.SeedMemberAsync(guild, $"account{n}", nick: $"[{sid}] former{n}");
        var medium = await _seed.SeedMemberAsync(guild, $"medium{n}");
        var unmapped = await _seed.SeedGuildAsync(null);
        var unlinked = await _seed.SeedMemberAsync(unmapped, $"former{n}");

        var runs = await Task.WhenAll(AutoLinkAsync(guild), AutoLinkAsync(guild));
        runs.Sum().Should().Be(1);
        (await AutoLinkAsync(guild)).Should().Be(0);
        (await AutoLinkAsync(unmapped)).Should().Be(0);
        var link = (await _seed.ReadAsync(db => db.EntityLinks.AsNoTracking()
            .Where(l => l.Provider == LinkProviders.Discord && l.Value == strong).ToListAsync())).Should().ContainSingle().Subject;
        link.AuthorUsername.Should().Be(DiscordSuggestionService.AutomaticAuthor);
        link.AuthorApiUserId.Should().Be(0);
        var entity = await _seed.ReadAsync(db => db.TrackedEntities.SingleAsync(e => e.Id == link.TrackedEntityId));
        entity.CitizenId.Should().Be(Cid(n));
        entity.CurrentHandle.Should().Be($"Current{n}");
        (await _seed.ReadAsync(db => db.EntityLinks.AnyAsync(l => l.Value == medium || l.Value == unlinked))).Should().BeFalse();

        using var client = await factory.SignedInClientAsync($"auto-link-read-{n}");
        var suggestions = await client.GetFromJsonAsync<JsonElement>($"/api/discord/guilds/{guild}/suggestions");
        var remaining = suggestions.EnumerateArray().Should().ContainSingle().Subject;
        remaining.GetProperty("discordUserId").GetString().Should().Be(medium);
        remaining.GetProperty("confidence").GetString().Should().Be("medium");
    }

    [Fact]
    public async Task AllStrongSuggestionsOfOneAccountAreValidatedTogether()
    {
        var n = Next();
        var sid = $"AL{n}";
        await _seed.SeedRosterAsync(sid, $"PilotA{n}", Cid(n), null);
        await _seed.SeedRosterAsync(sid, $"PilotB{n}", Cid(n, 1), null);
        var guild = await _seed.SeedGuildAsync(sid);
        var account = await _seed.SeedMemberAsync(guild, $"account{n}", nick: $"PilotA{n} PilotB{n}");

        (await AutoLinkAsync(guild)).Should().Be(2);
        var people = await _seed.ReadAsync(db => (
            from link in db.EntityLinks
            join person in db.TrackedEntities on link.TrackedEntityId equals person.Id
            where link.Provider == LinkProviders.Discord && link.Value == account
            select person.CitizenId).ToListAsync());
        people.Should().BeEquivalentTo(new int?[] { Cid(n), Cid(n, 1) });
        (await AutoLinkAsync(guild)).Should().Be(0);
    }

    [Fact]
    public async Task IgnoredOptedOutBotDepartedAndAlreadyLinkedAccountsArePreserved()
    {
        var n = Next();
        var sid = $"AL{n}";
        var guild = await _seed.SeedGuildAsync(sid);
        var ids = new List<string>();
        for (var k = 0; k < 7; k++)
        {
            var handle = $"Skip{n}_{k}";
            await _seed.SeedRosterAsync(sid, handle, Cid(n, k), null);
            ids.Add(await _seed.SeedMemberAsync(guild, handle, bot: k == 1, left: k == 2));
        }
        var existing = await _seed.SeedPersonAsync(null, $"Manual{n}", null, ids[3]);
        await _seed.WithDbAsync(async db =>
        {
            db.DiscordOptOuts.Add(new DiscordOptOut { DiscordUserId = ids[4], ByApiUserId = 0, ByUsername = "seed", CreatedAt = DateTime.UtcNow });
            db.DiscordLinkRejections.Add(new DiscordLinkRejection { DiscordUserId = ids[5], CitizenKey = Cid(n, 5).ToString(), ByApiUserId = 0, ByUsername = "seed", CreatedAt = DateTime.UtcNow });
            db.DiscordLinkRejections.Add(new DiscordLinkRejection { DiscordUserId = ids[6], CitizenKey = $"h:skip{n}_6", ByApiUserId = 0, ByUsername = "seed", CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        });

        (await AutoLinkAsync(guild)).Should().Be(1);
        var links = await _seed.ReadAsync(db => db.EntityLinks.Where(l => l.Provider == LinkProviders.Discord && ids.Contains(l.Value)).ToListAsync());
        links.Select(l => l.Value).Should().BeEquivalentTo(new[] { ids[0], ids[3] });
        links.Single(l => l.Value == ids[3]).TrackedEntityId.Should().Be(existing);
        links.Single(l => l.Value == ids[3]).AuthorUsername.Should().Be("seed");
    }

    [Fact]
    public async Task ADeletedAutomaticLink_IsNotCreatedAgain_NorSuggested()
    {
        var n = Next();
        var sid = $"AL{n}";
        await _seed.SeedRosterAsync(sid, $"Gone{n}", Cid(n), null);
        await SeedUserAsync(Cid(n), $"Gone{n}");
        var guild = await _seed.SeedGuildAsync(sid);
        var account = await _seed.SeedMemberAsync(guild, $"account{n}", nick: $"Gone{n}");
        (await AutoLinkAsync(guild)).Should().Be(1);
        var linkId = await _seed.ReadAsync(db => db.EntityLinks
            .Where(l => l.Provider == LinkProviders.Discord && l.Value == account).Select(l => l.Id).SingleAsync());

        using var admin = await factory.SignedInClientAsync($"auto-link-unlink-{n}", isAdmin: true);
        (await admin.DeleteAsync($"/api/links/{linkId}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await AutoLinkAsync(guild)).Should().Be(0);
        (await HasLinkAsync(account)).Should().BeFalse();
        var suggestions = await admin.GetFromJsonAsync<JsonElement>($"/api/discord/guilds/{guild}/suggestions");
        suggestions.EnumerateArray().Should().BeEmpty();
    }

    [Fact]
    public async Task ADeletedLink_StaysRefused_WhenTheRosterRowHasNoCitizenId()
    {
        // The person behind the link is known by citizen id, but the strong match comes from a
        // roster row without one: the refusal must also hold under the handle.
        var n = Next();
        var sid = $"AL{n}";
        await _seed.SeedRosterAsync(sid, $"Bare{n}", null, null);
        var guild = await _seed.SeedGuildAsync(sid);
        var account = await _seed.SeedMemberAsync(guild, $"account{n}", nick: $"Bare{n}");
        var person = await _seed.SeedPersonAsync(Cid(n), $"Bare{n}", null, account);
        var linkId = await _seed.ReadAsync(db => db.EntityLinks
            .Where(l => l.TrackedEntityId == person && l.Value == account).Select(l => l.Id).SingleAsync());

        using var admin = await factory.SignedInClientAsync($"auto-link-bare-{n}", isAdmin: true);
        (await admin.DeleteAsync($"/api/links/{linkId}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await AutoLinkAsync(guild)).Should().Be(0);
        (await HasLinkAsync(account)).Should().BeFalse();
    }

    [Fact]
    public async Task DeletingAnotherProvidersLink_RefusesNoDiscordPair()
    {
        var n = Next();
        var uexId = $"uex-{n}";
        var person = await _seed.SeedPersonAsync(Cid(n), $"Trader{n}", null);
        var linkId = await _seed.ReadAsync(async db =>
        {
            var link = new EntityLink
            {
                TrackedEntityId = person, Provider = LinkProviders.Uex, Value = uexId,
                AuthorApiUserId = 0, AuthorUsername = "seed", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            db.EntityLinks.Add(link);
            await db.SaveChangesAsync();
            return link.Id;
        });

        using var admin = await factory.SignedInClientAsync($"auto-link-uex-{n}", isAdmin: true);
        (await admin.DeleteAsync($"/api/links/{linkId}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await _seed.ReadAsync(db => db.DiscordLinkRejections.AnyAsync(r => r.DiscordUserId == uexId))).Should().BeFalse();
    }

    [Fact]
    public async Task AMembersOwnTag_MakesAStrongLink_OnAnUnmappedServer()
    {
        var n = Next();
        var sid = $"AT{n}";
        await _seed.SeedOrgAsync(sid, $"Tagged corpo {n}");
        await _seed.SeedRosterAsync(sid, $"Tagged{n}", Cid(n), null);
        var guild = await _seed.SeedGuildAsync(null);
        var account = await _seed.SeedMemberAsync(guild, $"account{n}", nick: $"[{sid.ToLowerInvariant()}] Tagged{n}");

        using var client = await factory.SignedInClientAsync($"auto-link-tag-{n}");
        var suggestion = (await client.GetFromJsonAsync<JsonElement>($"/api/discord/guilds/{guild}/suggestions"))
            .EnumerateArray().Should().ContainSingle().Subject;
        suggestion.GetProperty("confidence").GetString().Should().Be("strong");
        suggestion.GetProperty("strongVia").GetString().Should().Be("tag");
        suggestion.GetProperty("strongOrgSid").GetString().Should().Be(sid);

        (await AutoLinkAsync(guild)).Should().Be(1);
        (await HasLinkAsync(account)).Should().BeTrue();
    }

    [Fact]
    public async Task OnlyTheMembersOwnTag_AndOnlyThatCorposRoster_MakeAMatchStrong()
    {
        var n = Next();
        var abc = $"AT{n}";
        var xyz = $"AX{n}";
        await _seed.SeedOrgAsync(abc, abc);
        await _seed.SeedOrgAsync(xyz, xyz);
        await _seed.SeedRosterAsync(abc, $"Inabc{n}", Cid(n), null);
        await _seed.SeedRosterAsync(abc, $"Other{n}", Cid(n, 1), null);
        await SeedUserAsync(Cid(n), $"Inabc{n}");
        await SeedUserAsync(Cid(n, 1), $"Other{n}");
        var guild = await _seed.SeedGuildAsync(null);
        // Tagged with another corpo, on whose roster the handle is not.
        var wrongTag = await _seed.SeedMemberAsync(guild, $"wrong{n}", nick: $"[{xyz}] Inabc{n}");
        // Untagged, while another member carries the tag of the corpo they belong to.
        var untagged = await _seed.SeedMemberAsync(guild, $"untagged{n}", nick: $"Other{n}");
        await _seed.SeedMemberAsync(guild, $"carrier{n}", nick: $"[{abc}] Nobody{n}");

        using var client = await factory.SignedInClientAsync($"auto-link-own-tag-{n}");
        var suggestions = (await client.GetFromJsonAsync<JsonElement>($"/api/discord/guilds/{guild}/suggestions"))
            .EnumerateArray().ToList();
        suggestions.Select(s => (s.GetProperty("discordUserId").GetString(), s.GetProperty("confidence").GetString()))
            .Should().BeEquivalentTo(new[] { (wrongTag, "medium"), (untagged, "medium") });
        suggestions.Should().OnlyContain(s => s.GetProperty("strongVia").ValueKind == JsonValueKind.Null);
        (await AutoLinkAsync(guild)).Should().Be(0);
    }

    [Fact]
    public async Task AStrongSuggestion_SaysWhetherTheServerOrTheTagMadeItStrong()
    {
        var n = Next();
        var sid = $"AL{n}";
        var ally = $"AX{n}";
        await _seed.SeedOrgAsync(ally, ally);
        await _seed.SeedRosterAsync(sid, $"Home{n}", Cid(n), null);
        await _seed.SeedRosterAsync(ally, $"Ally{n}", Cid(n, 1), null);
        var guild = await _seed.SeedGuildAsync(sid);
        var home = await _seed.SeedMemberAsync(guild, $"home{n}", nick: $"Home{n}");
        var allied = await _seed.SeedMemberAsync(guild, $"allied{n}", nick: $"Ally{n} | {ally}");

        using var client = await factory.SignedInClientAsync($"auto-link-via-{n}");
        var bySource = (await client.GetFromJsonAsync<JsonElement>($"/api/discord/guilds/{guild}/suggestions"))
            .EnumerateArray().ToDictionary(
                s => s.GetProperty("discordUserId").GetString()!,
                s => (s.GetProperty("strongVia").GetString(), s.GetProperty("strongOrgSid").GetString()));

        bySource[home].Should().Be(("server", sid));
        bySource[allied].Should().Be(("tag", ally));
    }

    [Fact]
    public async Task ThePeriodicSweepAlsoCoversUnmappedServers()
    {
        var n = Next();
        var sid = $"AT{n}";
        await _seed.SeedOrgAsync(sid, sid);
        await _seed.SeedRosterAsync(sid, $"Swept{n}", Cid(n), null);
        var guild = await _seed.SeedGuildAsync(null);
        var account = await _seed.SeedMemberAsync(guild, $"account{n}", nick: $"[{sid}] Swept{n}");

        using var worker = NewWorker(new DiscordAutoLinkQueue(), TimeProvider.System, TimeSpan.FromDays(1));
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await EventuallyAsync(() => HasLinkAsync(account));
        }
        finally { await worker.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task TheWorkerLinksExistingMembersOnStartup_AndDrainsSeveralBatches()
    {
        var n = Next();
        var sid = $"AL{n}";
        var guild = await _seed.SeedGuildAsync(sid);
        var ids = new List<string>();
        for (var k = 0; k < DiscordSuggestionService.AutomaticBatchSize + 1; k++)
        {
            var handle = $"Batch{n}_{k}";
            await _seed.SeedRosterAsync(sid, handle, null, null);
            ids.Add(await _seed.SeedMemberAsync(guild, handle));
        }
        using var worker = NewWorker(new DiscordAutoLinkQueue(), TimeProvider.System, TimeSpan.FromDays(1));
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await EventuallyAsync(async () => (await _seed.ReadAsync(db => db.EntityLinks.CountAsync(l =>
                l.Provider == LinkProviders.Discord && ids.Contains(l.Value)))) == ids.Count);
        }
        finally { await worker.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task IngestAndOrgMappingWakeTheWorker_WithoutWaitingForThePeriodicSweep()
    {
        var n = Next();
        var sid = $"AL{n}";
        await _seed.SeedOrgAsync(sid, sid);
        await _seed.SeedRosterAsync(sid, $"Ready{n}", Cid(n), null);
        await _seed.SeedRosterAsync(sid, $"Ingest{n}", Cid(n, 1), null);
        await _seed.SeedRosterAsync(sid, $"Map{n}", Cid(n, 2), null);
        var guild = await _seed.SeedGuildAsync(sid);
        var ready = await _seed.SeedMemberAsync(guild, $"Ready{n}");
        var unmapped = await _seed.SeedGuildAsync(null);
        var mappedMember = await _seed.SeedMemberAsync(unmapped, $"Map{n}");
        var queue = factory.Services.GetRequiredService<DiscordAutoLinkQueue>();
        using var worker = NewWorker(queue, TimeProvider.System, TimeSpan.FromDays(1));
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await EventuallyAsync(() => HasLinkAsync(ready));
            var (ingest, _, _) = await IngestClientAsync(factory, $"auto-link-ingest-{n}");
            using (ingest)
            {
                var account = NewSnowflake();
                using var response = await PostSyncAsync(ingest, guild,
                    Sync(guild, [Member(ready, $"Ready{n}"), Member(account, $"Ingest{n}")], durationMs: 0));
                response.StatusCode.Should().Be(HttpStatusCode.OK);
                await EventuallyAsync(() => HasLinkAsync(account));
            }
            using var client = await factory.SignedInClientAsync($"auto-link-map-{n}");
            (await client.PutAsJsonAsync($"/api/discord/guilds/{unmapped}/org", new { orgSid = sid }))
                .StatusCode.Should().Be(HttpStatusCode.OK);
            await EventuallyAsync(() => HasLinkAsync(mappedMember));
        }
        finally { await worker.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task ThePeriodicSweepAlsoPicksUpLaterRsiRosterChanges()
    {
        var n = Next();
        var sid = $"AL{n}";
        await _seed.SeedRosterAsync(sid, $"Ready{n}", Cid(n), null);
        await _seed.SeedRosterAsync(sid, $"Later{n}", Cid(n, 1), null, active: false);
        var guild = await _seed.SeedGuildAsync(sid);
        var ready = await _seed.SeedMemberAsync(guild, $"Ready{n}");
        var later = await _seed.SeedMemberAsync(guild, $"Later{n}");
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var worker = NewWorker(new DiscordAutoLinkQueue(), clock, TimeSpan.FromMinutes(1));
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await EventuallyAsync(() => HasLinkAsync(ready));
            (await HasLinkAsync(later)).Should().BeFalse();
            await _seed.WithDbAsync(async db =>
            {
                var member = await db.OrganizationMembers.SingleAsync(m => m.OrgSid == sid && m.UserHandle == $"Later{n}");
                member.IsActive = true;
                await db.SaveChangesAsync();
            });
            await EventuallyAsync(() => HasLinkAsync(later), () => clock.Advance(TimeSpan.FromMinutes(1)));
        }
        finally { await worker.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task CancellationWhileAnotherWriterHoldsTheGate_CreatesNoLink()
    {
        var n = Next();
        var sid = $"AL{n}";
        await _seed.SeedRosterAsync(sid, $"Wait{n}", Cid(n), null);
        var guild = await _seed.SeedGuildAsync(sid);
        var account = await _seed.SeedMemberAsync(guild, $"Wait{n}");
        using var held = await factory.Services.GetRequiredService<DiscordWriteGate>().EnterAsync(CancellationToken.None);
        using var stop = new CancellationTokenSource();
        var pending = AutoLinkAsync(guild, stop.Token);
        await Task.Delay(30);
        pending.IsCompleted.Should().BeFalse();
        stop.Cancel();
        Func<Task> wait = async () => { await pending; };
        await wait.Should().ThrowAsync<OperationCanceledException>();
        (await HasLinkAsync(account)).Should().BeFalse();
    }

    private async Task<int> AutoLinkAsync(string guildId, CancellationToken ct = default)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<DiscordSuggestionService>().AutoLinkStrongAsync(guildId, ct);
    }

    private Task<bool> HasLinkAsync(string id) => _seed.ReadAsync(db => db.EntityLinks.AnyAsync(l => l.Provider == LinkProviders.Discord && l.Value == id));

    private DiscordAutoLinkService NewWorker(DiscordAutoLinkQueue queue, TimeProvider time, TimeSpan interval) => new(
        factory.Services.GetRequiredService<IServiceScopeFactory>(), queue, Microsoft.Extensions.Options.Options.Create(new DiscordOptions()),
        time, NullLogger<DiscordAutoLinkService>.Instance) { SweepInterval = interval };

    private static async Task EventuallyAsync(Func<Task<bool>> condition, Action? advance = null)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!await condition())
        {
            advance?.Invoke();
            await Task.Delay(50, timeout.Token);
        }
    }

    private Task SeedUserAsync(int citizenId, string handle) => _seed.WithDbAsync(async db =>
    {
        db.Users.Add(new User { CitizenId = citizenId, UserHandle = handle, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
    });
}
