### Task C5: Link suggestions, links and rejections

**Files:**
- Create: `src/Collector.Api/Services/Discord/DiscordHandleLookup.cs`
- Create: `src/Collector.Api/Services/Discord/DiscordSuggestionService.cs`
- Create: `src/Collector.Api/Controllers/DiscordRostersController.Links.cs`
- Modify: `src/Collector.Api/Controllers/DiscordRostersController.cs` (class declaration created in C2: add the `partial` modifier)
- Modify: `src/Collector.Api/Dtos/Discord/DiscordRosterDtos.cs` (append at the end of the file)
- Modify: `src/Collector.Api/Extensions/ServiceCollectionExtensions.cs` (`AddApiServices`, after `services.AddHostedService<AudioOrphanSweeper>();`)
- Modify: `src/Collector.Api/Controllers/LinksController.cs` (usings; `CreateLink`, after the empty-value check)
- Test: `src/Collector.Api.Tests/Discord/DiscordHandleLookupTests.cs`
- Test: `src/Collector.Api.Tests/Discord/DiscordSuggestionTests.cs`
- Test: `src/Collector.Api.Tests/Discord/DiscordLinkTests.cs`

**Interfaces:**
- Consumes:
  - CONTRACTS § 1: entities `DiscordGuild`, `DiscordAccount`, `DiscordMember`, `DiscordLinkRejection` (namespace `Collector.Models`), DbSets `DiscordGuilds`, `DiscordAccounts`, `DiscordMembers`, `DiscordLinkRejections`; indexes `IX_users_UserHandle_NoCase`, `IX_user_handle_history_UserHandle_NoCase`, `IX_entity_links_Provider_Value` (migration `AddDiscordRosters`).
  - CONTRACTS § 0: `Collector.Discord.DiscordSnowflake.IsValid(string? s)`.
  - CONTRACTS § 7 (task C1): `Collector.Discord.HandleTokenizer.Candidates(string? nick, string? globalName, string username) : IReadOnlyList<string>`.
  - CONTRACTS § 4/§ 5 (lot A): `Collector.Api.Services.Discord.DiscordWriteGate.TryEnterAsync(TimeSpan timeout, CancellationToken ct) : Task<IDisposable?>`; `Collector.Api.Errors.ServiceUnavailableException(string message, int retryAfterSeconds, string? code = "busy")`; test kit `DiscordTestKit.NewSnowflake()`.
  - Task C2: `DiscordRostersController` (`[ApiController, Route("api"), Authorize]`, `src/Collector.Api/Controllers/DiscordRostersController.cs`) and the DTO file `src/Collector.Api/Dtos/Discord/DiscordRosterDtos.cs` (file-scoped namespace `Collector.Api.Dtos.Discord`).
  - Existing: `IEntityResolver.ResolveOrCreateAsync(int? citizenId, string? handle, string? displayName, CancellationToken)`, `IEntityLinkRepository.GetByEntityProviderValueAsync`, `CurrentUserAccessor`, `NotFoundException`, `ValidationException`, `ForbiddenException`, `LinkProviders.Discord`, `ApiFactory`, `ApiCollection`.
- Produces:
  - `Collector.Api.Services.Discord.DiscordHandleLookup` (scoped): `const int BatchSize = 500`; `Task<IReadOnlyList<HandleMatch>> FindAsync(IEnumerable<string> tokens, string? orgSid, CancellationToken ct)`; `Task<RsiPerson?> ResolvePersonAsync(int? citizenId, string handle, CancellationToken ct)`; records `HandleMatch(string MatchedValue, string Handle, int? CitizenId, string? DisplayName, bool Strong)` and `RsiPerson(int? CitizenId, string Handle, string? DisplayName)`.
  - `Collector.Api.Services.Discord.DiscordSuggestionService` (scoped): `GetSuggestionsAsync(string guildId, CancellationToken)`, `LinkAsync(CreateDiscordLinkRequest, CancellationToken)`, `RejectAsync(CreateDiscordLinkRejectionRequest, CancellationToken) : Task<long>`, `DeleteRejectionAsync(long id, CancellationToken)`, `static string CitizenKey(int? citizenId, string handle)`, `static string HandleKey(string handle)`, `const int MaxHandleLength = 98`.
  - DTOs in `Collector.Api.Dtos.Discord`: `DiscordSuggestionDto`, `DiscordSuggestionConfidence`, `CreateDiscordLinkRequest(string DiscordUserId, int? CitizenId, string Handle)`, `CreateDiscordLinkRejectionRequest(string DiscordUserId, int? CitizenId, string Handle)`, `DiscordLinkCreatedDto { EntityId, Handle }`, `DiscordLinkRejectionCreatedDto { Id }`.
  - Routes (CONTRACTS § 7): `GET api/discord/guilds/{guildId}/suggestions`, `POST api/discord/links` (201), `POST api/discord/link-rejections` (201), `DELETE api/discord/link-rejections/{id}` (204/403/404); `POST api/users/{handle}/links` now answers 400 for a non-snowflake `discord` value.
  - `DiscordRostersController` is `partial`; C6 adds its own part file.

- [ ] **Step 1: Write the failing lookup test**

Create `src/Collector.Api.Tests/Discord/DiscordHandleLookupTests.cs`:

```csharp
using System.Collections.Concurrent;
using System.Data.Common;
using Collector.Api.Services.Discord;
using Collector.Data;
using Collector.Models;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Collector.Api.Tests.Discord;

/// <summary>
/// Discord name tokens are looked up among RSI handles regardless of case, in bounded
/// batches served by the NOCASE indexes, and always come back with the canonical current
/// handle and the citizen id read from the database.
/// </summary>
public sealed class DiscordHandleLookupTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SqlCapture _sql = new();
    private TrackerDbContext _db = null!;

    public async Task InitializeAsync()
    {
        _connection.Open();
        _db = new TrackerDbContext(new DbContextOptionsBuilder<TrackerDbContext>()
            .UseSqlite(_connection).AddInterceptors(_sql).Options);
        await _db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        _connection.Dispose();
    }

    [Fact]
    public async Task Tokens_GoInBatchesOf500_ThroughTheNoCaseIndexes()
    {
        var now = DateTime.UtcNow;
        _db.Users.AddRange(
            new User { CitizenId = 1, UserHandle = "Batch0003", CreatedAt = now, UpdatedAt = now },
            new User { CitizenId = 2, UserHandle = "Renamed1199", CreatedAt = now, UpdatedAt = now });
        _db.UserHandleHistories.Add(new UserHandleHistory
        {
            CitizenId = 2, UserHandle = "Batch1199", FirstSeen = now.AddYears(-2), LastSeen = now.AddYears(-1),
        });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        var tokens = Enumerable.Range(0, 1200).Select(i => $"batch{i:D4}").ToList();
        _sql.Commands.Clear();

        var matches = await new DiscordHandleLookup(_db).FindAsync(tokens, orgSid: null, default);

        matches.Should().BeEquivalentTo(new[]
        {
            new HandleMatch("Batch0003", "Batch0003", 1, null, Strong: false),
            new HandleMatch("Batch1199", "Renamed1199", 2, null, Strong: false),
        });
        var lookups = _sql.Commands.Where(c => c.Sql.Contains("COLLATE NOCASE IN (")).ToList();
        lookups.Where(c => c.Sql.Contains("FROM users")).Should().HaveCount(3);
        lookups.Where(c => c.Sql.Contains("FROM user_handle_history")).Should().HaveCount(3);
        lookups.Should().OnlyContain(c => c.Parameters.Count <= DiscordHandleLookup.BatchSize);
        lookups.Max(c => c.Parameters.Count).Should().Be(DiscordHandleLookup.BatchSize);
        (await PlanAsync(lookups.First(c => c.Sql.Contains("FROM users"))))
            .Should().Contain(d => d.Contains("IX_users_UserHandle_NoCase"));
        (await PlanAsync(lookups.First(c => c.Sql.Contains("FROM user_handle_history"))))
            .Should().Contain(d => d.Contains("IX_user_handle_history_UserHandle_NoCase"));
    }

    [Fact]
    public async Task Matches_CarryTheCanonicalCurrentHandle_AndTheMappedRosterMakesThemStrong()
    {
        var now = DateTime.UtcNow;
        _db.Users.AddRange(
            new User { CitizenId = 10, UserHandle = "Current10", DisplayName = "Ten", CreatedAt = now, UpdatedAt = now },
            new User { CitizenId = 30, UserHandle = "Rostered30", DisplayName = "Thirty", CreatedAt = now, UpdatedAt = now });
        _db.UserHandleHistories.AddRange(
            new UserHandleHistory { CitizenId = 10, UserHandle = "Former10", FirstSeen = now.AddYears(-3), LastSeen = now.AddYears(-2) },
            new UserHandleHistory { CitizenId = 20, UserHandle = "OnlyOld20", FirstSeen = now.AddYears(-3), LastSeen = now.AddYears(-2) },
            new UserHandleHistory { CitizenId = 20, UserHandle = "OnlyNew20", FirstSeen = now.AddYears(-1), LastSeen = now });
        var formerMember = new OrganizationMember { OrgSid = "ORG", UserHandle = "Current10", CitizenId = 10, Timestamp = now, IsActive = true };
        _db.OrganizationMembers.AddRange(
            new OrganizationMember { OrgSid = "ORG", UserHandle = "Rostered30", CitizenId = null, Timestamp = now, IsActive = true },
            formerMember);
        await _db.SaveChangesAsync();
        // IsActive has a database default of true: EF omits an inserted false (the CLR default)
        // and the row would come back active. Insert it active, then update it.
        formerMember.IsActive = false;
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var matches = await new DiscordHandleLookup(_db)
            .FindAsync(["former10", "onlyold20", "ROSTERED30", "current10"], "ORG", default);

        matches.Should().BeEquivalentTo(new[]
        {
            // A former handle: the citizen's current handle, from users.
            new HandleMatch("Former10", "Current10", 10, "Ten", Strong: false),
            // A former handle of a citizen without a users row: their latest handle.
            new HandleMatch("OnlyOld20", "OnlyNew20", 20, null, Strong: false),
            // The mapped org's active roster is strong; the citizen id comes from users.
            new HandleMatch("Rostered30", "Rostered30", 30, "Thirty", Strong: true),
            new HandleMatch("Rostered30", "Rostered30", 30, "Thirty", Strong: false),
            // An inactive roster row is not strong.
            new HandleMatch("Current10", "Current10", 10, "Ten", Strong: false),
        });
    }

    [Fact]
    public async Task APerson_IsReadBackRegardlessOfCase_FromUsersThenHistoryThenTheRoster()
    {
        var now = DateTime.UtcNow;
        _db.Users.Add(new User { CitizenId = 40, UserHandle = "Pilote40", CreatedAt = now, UpdatedAt = now });
        _db.UserHandleHistories.Add(new UserHandleHistory
        {
            CitizenId = 40, UserHandle = "OldPilote40", FirstSeen = now.AddYears(-2), LastSeen = now.AddYears(-1),
        });
        _db.OrganizationMembers.AddRange(
            new OrganizationMember { OrgSid = "ORG", UserHandle = "Roster50", CitizenId = null, Timestamp = now, IsActive = true },
            new OrganizationMember { OrgSid = "ORG", UserHandle = "Roster60", CitizenId = 60, Timestamp = now, IsActive = true });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        var lookup = new DiscordHandleLookup(_db);

        (await lookup.ResolvePersonAsync(null, "pilote40", default)).Should().Be(new RsiPerson(40, "Pilote40", null));
        (await lookup.ResolvePersonAsync(null, "OLDPILOTE40", default)).Should().Be(new RsiPerson(40, "Pilote40", null));
        (await lookup.ResolvePersonAsync(40, "anything", default)).Should().Be(new RsiPerson(40, "Pilote40", null));
        (await lookup.ResolvePersonAsync(null, "Roster50", default)).Should().Be(new RsiPerson(null, "Roster50", null));
        // The roster's own spelling comes back, whatever the case sent.
        (await lookup.ResolvePersonAsync(null, "ROSTER50", default)).Should().Be(new RsiPerson(null, "Roster50", null));
        (await lookup.ResolvePersonAsync(60, "Roster60", default)).Should().Be(new RsiPerson(60, "Roster60", null));
        (await lookup.ResolvePersonAsync(null, "Nobody", default)).Should().BeNull();
    }

    private async Task<IReadOnlyList<string>> PlanAsync(CapturedCommand command)
    {
        await using var explain = _connection.CreateCommand();
        explain.CommandText = "EXPLAIN QUERY PLAN " + command.Sql;
        foreach (var (name, value) in command.Parameters) explain.Parameters.AddWithValue(name, value ?? DBNull.Value);
        var details = new List<string>();
        await using var reader = await explain.ExecuteReaderAsync();
        while (await reader.ReadAsync()) details.Add(reader.GetString(3));
        return details;
    }

    private sealed record CapturedCommand(string Sql, IReadOnlyList<(string Name, object? Value)> Parameters);

    private sealed class SqlCapture : DbCommandInterceptor
    {
        public ConcurrentQueue<CapturedCommand> Commands { get; } = new();

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Enqueue(new CapturedCommand(
                command.CommandText,
                command.Parameters.Cast<DbParameter>().Select(p => (p.ParameterName, (object?)p.Value)).ToList()));
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test src/Collector.Api.Tests --filter "FullyQualifiedName~DiscordHandleLookupTests"`

Expected: the build fails with `error CS0246: The type or namespace name 'DiscordHandleLookup' could not be found` (also `HandleMatch`, `RsiPerson`).

- [ ] **Step 3: Write the lookup**

Create `src/Collector.Api/Services/Discord/DiscordHandleLookup.cs`:

```csharp
using Collector.Data;
using Microsoft.EntityFrameworkCore;

namespace Collector.Api.Services.Discord;

/// <summary>
/// A Discord name token equal to an RSI handle. <see cref="Handle"/> is the canonical current
/// handle read from the database, never the token: the matched row's own handle, or, for a
/// former handle, the citizen's current one.
/// </summary>
/// <param name="MatchedValue">The handle stored on the row that matched (the token's letters, in the row's case).</param>
/// <param name="Strong">True when the row is an active member of the org the guild is mapped to.</param>
public sealed record HandleMatch(string MatchedValue, string Handle, int? CitizenId, string? DisplayName, bool Strong);

/// <summary>An RSI person as the database knows them now: citizen id when known, and current handle.</summary>
public sealed record RsiPerson(int? CitizenId, string Handle, string? DisplayName);

/// <summary>
/// Finds the RSI people behind Discord names, regardless of case (spec § 10.1). Tokens go in
/// batches of <see cref="BatchSize"/> into <c>UserHandle COLLATE NOCASE IN (…)</c>, which the
/// NOCASE indexes of users and user_handle_history serve; RSI handles are ASCII, so NOCASE
/// compares them exactly. Every match carries the handle and citizen id read from the
/// database, so a caller never writes a Discord spelling or a former handle anywhere.
/// </summary>
public sealed class DiscordHandleLookup(TrackerDbContext db)
{
    /// <summary>Tokens per lookup query: bounds the number of SQL parameters.</summary>
    public const int BatchSize = 500;

    private const string TokenList = "@tokens";

    // The mapped org's active roster. SQLite picks either the (OrgSid, IsActive) index or
    // IX_organization_members_UserHandle_NoCase (migration IndexCleanup); one org's active rows
    // are a few thousand at most either way.
    private const string OrgMembersSql = """
        SELECT UserHandle AS Handle, CitizenId, DisplayName, Timestamp AS SeenAt
        FROM organization_members
        WHERE OrgSid = {0} AND IsActive = 1 AND UserHandle COLLATE NOCASE IN (@tokens)
        """;

    private const string UsersSql = """
        SELECT UserHandle AS Handle, CitizenId, DisplayName, UpdatedAt AS SeenAt
        FROM users
        WHERE UserHandle COLLATE NOCASE IN (@tokens)
        """;

    private const string HistorySql = """
        SELECT UserHandle AS Handle, CitizenId, NULL AS DisplayName, LastSeen AS SeenAt
        FROM user_handle_history
        WHERE UserHandle COLLATE NOCASE IN (@tokens)
        """;

    /// <summary>
    /// Every RSI person one of <paramref name="tokens"/> names: active members of
    /// <paramref name="orgSid"/> (strong, only when the guild is mapped), current handles in
    /// users and former handles in user_handle_history (medium). A token may yield several
    /// matches; the caller merges them per person.
    /// </summary>
    public async Task<IReadOnlyList<HandleMatch>> FindAsync(
        IEnumerable<string> tokens, string? orgSid, CancellationToken ct)
    {
        var distinct = tokens.Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var roster = new List<HandleRow>();
        var users = new List<HandleRow>();
        var former = new List<HandleRow>();
        foreach (var batch in distinct.Chunk(BatchSize))
        {
            if (orgSid is not null) roster.AddRange(await QueryAsync(OrgMembersSql, batch, orgSid, ct));
            users.AddRange(await QueryAsync(UsersSql, batch, null, ct));
            former.AddRange(await QueryAsync(HistorySql, batch, null, ct));
        }

        // A handle given up and taken again is held by two users rows until the former owner
        // is read again: the profile read last holds it (as in UserRepository.GetByHandleAsync).
        var userByHandle = users
            .OrderByDescending(u => u.SeenAt)
            .DistinctBy(u => u.Handle, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(u => u.Handle, StringComparer.OrdinalIgnoreCase);

        var matches = new List<HandleMatch>();
        foreach (var row in roster)
        {
            // Roster rows may predate the citizen id: users fills it in when it knows the handle.
            userByHandle.TryGetValue(row.Handle, out var profile);
            matches.Add(new HandleMatch(row.Handle, row.Handle, row.CitizenId ?? profile?.CitizenId,
                row.DisplayName ?? profile?.DisplayName, Strong: true));
        }
        foreach (var row in userByHandle.Values)
            matches.Add(new HandleMatch(row.Handle, row.Handle, row.CitizenId, row.DisplayName, Strong: false));

        if (former.Count > 0)
        {
            var current = await CurrentByCitizenIdAsync(
                former.Select(f => f.CitizenId!.Value).Distinct().ToList(), ct);
            foreach (var row in former
                         .OrderByDescending(f => f.SeenAt)
                         .DistinctBy(f => (f.CitizenId, f.Handle.ToLowerInvariant())))
            {
                if (current.TryGetValue(row.CitizenId!.Value, out var person))
                    matches.Add(new HandleMatch(row.Handle, person.Handle, person.CitizenId, person.DisplayName, Strong: false));
            }
        }
        return matches;
    }

    /// <summary>
    /// Reads a person back for a link: by citizen id when given and known, else by handle
    /// regardless of case in users, then in user_handle_history (followed to the citizen's
    /// current handle), then in organization_members (its latest row; each source has a NOCASE
    /// index). Null when no source knows them. The handle returned is always the stored one.
    /// </summary>
    public async Task<RsiPerson?> ResolvePersonAsync(int? citizenId, string handle, CancellationToken ct)
    {
        if (citizenId is int cid && await ByCitizenIdAsync(cid, ct) is { } known) return known;

        var user = await db.Users.AsNoTracking()
            .Where(u => EF.Functions.Collate(u.UserHandle, "NOCASE") == handle)
            .OrderByDescending(u => u.UpdatedAt)
            .Select(u => new RsiPerson(u.CitizenId, u.UserHandle, u.DisplayName))
            .FirstOrDefaultAsync(ct);
        if (user is not null) return user;

        var formerOwner = await db.UserHandleHistories.AsNoTracking()
            .Where(h => EF.Functions.Collate(h.UserHandle, "NOCASE") == handle)
            .OrderByDescending(h => h.LastSeen)
            .Select(h => (int?)h.CitizenId)
            .FirstOrDefaultAsync(ct);
        if (formerOwner is int formerCid) return await ByCitizenIdAsync(formerCid, ct);

        var member = await db.OrganizationMembers.AsNoTracking()
            .Where(m => m.UserHandle == handle)
            .OrderByDescending(m => m.Timestamp)
            .Select(m => new RsiPerson(m.CitizenId, m.UserHandle, m.DisplayName))
            .FirstOrDefaultAsync(ct);
        if (member?.CitizenId is int memberCid && await ByCitizenIdAsync(memberCid, ct) is { } enriched)
            return enriched;
        return member;
    }

    private async Task<RsiPerson?> ByCitizenIdAsync(int citizenId, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking()
            .Where(u => u.CitizenId == citizenId)
            .Select(u => new RsiPerson(u.CitizenId, u.UserHandle, u.DisplayName))
            .FirstOrDefaultAsync(ct);
        if (user is not null) return user;

        var latest = await db.UserHandleHistories.AsNoTracking()
            .Where(h => h.CitizenId == citizenId)
            .OrderByDescending(h => h.LastSeen)
            .Select(h => h.UserHandle)
            .FirstOrDefaultAsync(ct);
        if (latest is not null) return new RsiPerson(citizenId, latest, null);

        return await db.OrganizationMembers.AsNoTracking()
            .Where(m => m.CitizenId == citizenId)
            .OrderByDescending(m => m.Timestamp)
            .Select(m => new RsiPerson(m.CitizenId, m.UserHandle, m.DisplayName))
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>Current handle of each citizen: users first, else their latest handle in history.</summary>
    private async Task<Dictionary<int, RsiPerson>> CurrentByCitizenIdAsync(
        IReadOnlyCollection<int> citizenIds, CancellationToken ct)
    {
        var result = (await db.Users.AsNoTracking()
                .Where(u => citizenIds.Contains(u.CitizenId))
                .Select(u => new RsiPerson(u.CitizenId, u.UserHandle, u.DisplayName))
                .ToListAsync(ct))
            .ToDictionary(p => p.CitizenId!.Value);

        var missing = citizenIds.Where(id => !result.ContainsKey(id)).ToList();
        if (missing.Count == 0) return result;

        var history = await db.UserHandleHistories.AsNoTracking()
            .Where(h => missing.Contains(h.CitizenId))
            .Select(h => new { h.CitizenId, h.UserHandle, h.LastSeen })
            .ToListAsync(ct);
        foreach (var latest in history.GroupBy(h => h.CitizenId).Select(g => g.MaxBy(h => h.LastSeen)!))
            result[latest.CitizenId] = new RsiPerson(latest.CitizenId, latest.UserHandle, null);
        return result;
    }

    private async Task<List<HandleRow>> QueryAsync(string sql, string[] batch, string? orgSid, CancellationToken ct)
    {
        var args = new List<object>(batch.Length + 1);
        if (orgSid is not null) args.Add(orgSid);
        var first = args.Count;
        args.AddRange(batch);
        var placeholders = string.Join(", ", Enumerable.Range(first, batch.Length).Select(i => $"{{{i}}}"));
        return await db.Database
            .SqlQueryRaw<HandleRow>(sql.Replace(TokenList, placeholders), args.ToArray())
            .ToListAsync(ct);
    }

    /// <summary>One row of a lookup query (public so EF can map the raw SQL onto it).</summary>
    public sealed class HandleRow
    {
        public string Handle { get; set; } = "";
        public int? CitizenId { get; set; }
        public string? DisplayName { get; set; }
        public DateTime SeenAt { get; set; }
    }
}
```

- [ ] **Step 4: Run the lookup tests to verify they pass**

Run: `dotnet test src/Collector.Api.Tests --filter "FullyQualifiedName~DiscordHandleLookupTests"`

Expected: `Passed!  - Failed:     0, Passed:     3, Skipped:     0, Total:     3`

- [ ] **Step 5: Write the failing suggestion tests**

Create `src/Collector.Api.Tests/Discord/DiscordSuggestionTests.cs`:

```csharp
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Collector.Data;
using Collector.Models;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Collector.Api.Tests.Discord;

/// <summary>
/// GET api/discord/guilds/{guildId}/suggestions (spec § 10.1): Discord name tokens matched
/// against RSI handles regardless of case, carrying the canonical current handle and the
/// citizen id read from the database; strong only for the mapped org's active roster.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class DiscordSuggestionTests(ApiFactory factory)
{
    private static int _seq = 51_000;

    private static int Next() => Interlocked.Increment(ref _seq);

    /// <summary>A citizen id no other test uses.</summary>
    private static int Cid(int n, int k) => 1_510_000_000 + n * 10 + k;

    [Fact]
    public async Task ACaseMismatch_IsSuggested_WithTheCanonicalHandleAndCitizenId()
    {
        var n = Next();
        await SeedUserAsync(Cid(n, 0), $"Pilote{n}", "Pilote affiché");
        var guildId = await SeedGuildAsync(orgSid: null);
        var userId = await SeedMemberAsync(guildId, $"pilote{n}");
        var client = await factory.SignedInClientAsync($"c5-sugg-case-{n}");

        var suggestion = (await SuggestionsAsync(client, guildId)).Should().ContainSingle().Subject;

        suggestion.GetProperty("discordUserId").GetString().Should().Be(userId);
        suggestion.GetProperty("discordName").GetString().Should().Be($"pilote{n}");
        suggestion.GetProperty("matchedToken").GetString().Should().BeEquivalentTo($"pilote{n}");
        suggestion.GetProperty("handle").GetString().Should().Be($"Pilote{n}");
        suggestion.GetProperty("citizenId").GetInt32().Should().Be(Cid(n, 0));
        suggestion.GetProperty("displayName").GetString().Should().Be("Pilote affiché");
        suggestion.GetProperty("confidence").GetString().Should().Be("medium");
    }

    [Fact]
    public async Task AFormerHandle_IsSuggested_WithTheCurrentHandleAndCitizenId()
    {
        var n = Next();
        await SeedUserAsync(Cid(n, 0), $"NewHandle{n}", null);
        await SeedHistoryAsync(Cid(n, 0), $"OldHandle{n}", DateTime.UtcNow.AddYears(-1));
        await SeedHistoryAsync(Cid(n, 0), $"NewHandle{n}", DateTime.UtcNow);
        var guildId = await SeedGuildAsync(orgSid: null);
        var userId = await SeedMemberAsync(guildId, $"c5user{n}", nick: $"[CORP] oldhandle{n}");
        var client = await factory.SignedInClientAsync($"c5-sugg-old-{n}");

        var suggestion = (await SuggestionsAsync(client, guildId)).Should().ContainSingle().Subject;

        suggestion.GetProperty("discordUserId").GetString().Should().Be(userId);
        suggestion.GetProperty("discordName").GetString().Should().Be($"[CORP] oldhandle{n}");
        suggestion.GetProperty("matchedToken").GetString().Should().BeEquivalentTo($"oldhandle{n}");
        suggestion.GetProperty("handle").GetString().Should().Be($"NewHandle{n}");
        suggestion.GetProperty("citizenId").GetInt32().Should().Be(Cid(n, 0));
        suggestion.GetProperty("confidence").GetString().Should().Be("medium");
    }

    [Fact]
    public async Task InAMappedGuild_TheActiveRosterIsStrong_OtherMatchesMedium()
    {
        var n = Next();
        var sid = $"SG{n}";
        await SeedOrgMemberAsync(sid, $"Orgy{n}", Cid(n, 0), active: true);
        await SeedOrgMemberAsync(sid, $"Gone{n}", Cid(n, 1), active: false);
        await SeedUserAsync(Cid(n, 0), $"Orgy{n}", "Orgy");
        await SeedUserAsync(Cid(n, 1), $"Gone{n}", null);
        var mapped = await SeedGuildAsync(orgSid: sid);
        var strongId = await SeedMemberAsync(mapped, $"c5strong{n}", nick: $"[{sid}] orgy{n}");
        var mediumId = await SeedMemberAsync(mapped, $"gone{n}");
        var unmapped = await SeedGuildAsync(orgSid: null);
        var unmappedId = await SeedMemberAsync(unmapped, $"orgy{n}");
        var client = await factory.SignedInClientAsync($"c5-sugg-strong-{n}");

        var inMapped = await SuggestionsAsync(client, mapped);
        var inUnmapped = await SuggestionsAsync(client, unmapped);

        inMapped.Select(s => (
                s.GetProperty("discordUserId").GetString(),
                s.GetProperty("handle").GetString(),
                s.GetProperty("citizenId").GetInt32(),
                s.GetProperty("confidence").GetString()))
            .Should().Equal(
                (strongId, $"Orgy{n}", Cid(n, 0), "strong"),
                (mediumId, $"Gone{n}", Cid(n, 1), "medium"));
        var single = inUnmapped.Should().ContainSingle().Subject;
        single.GetProperty("discordUserId").GetString().Should().Be(unmappedId);
        single.GetProperty("confidence").GetString().Should().Be("medium");
    }

    [Fact]
    public async Task BotsDepartedLinkedAndRejectedMembers_AreNotSuggested()
    {
        var n = Next();
        string[] names = ["Kept", "Bot", "Left", "Linked", "RejCid", "RejHandle"];
        for (var k = 0; k < names.Length; k++) await SeedUserAsync(Cid(n, k), $"{names[k]}{n}", null);
        var guildId = await SeedGuildAsync(orgSid: null);
        var kept = await SeedMemberAsync(guildId, $"kept{n}");
        await SeedMemberAsync(guildId, $"bot{n}", bot: true);
        await SeedMemberAsync(guildId, $"left{n}", left: true);
        var linked = await SeedMemberAsync(guildId, $"linked{n}");
        var rejectedById = await SeedMemberAsync(guildId, $"rejcid{n}");
        var rejectedByHandle = await SeedMemberAsync(guildId, $"rejhandle{n}");
        await SeedLinkAsync(linked);
        await SeedRejectionAsync(rejectedById, Cid(n, 4).ToString(CultureInfo.InvariantCulture));
        // A rejection saved under the handle key still hides a suggestion that now has a citizen id.
        await SeedRejectionAsync(rejectedByHandle, $"h:rejhandle{n}");
        var client = await factory.SignedInClientAsync($"c5-sugg-skip-{n}");

        var suggestions = await SuggestionsAsync(client, guildId);

        suggestions.Select(s => s.GetProperty("discordUserId").GetString()).Should().Equal(kept);
    }

    [Fact]
    public async Task AnUnknownGuild_Returns404()
    {
        var client = await factory.SignedInClientAsync($"c5-sugg-404-{Next()}");

        var response = await client.GetAsync($"/api/discord/guilds/{DiscordTestKit.NewSnowflake()}/suggestions");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private static async Task<List<JsonElement>> SuggestionsAsync(HttpClient client, string guildId)
    {
        var response = await client.GetAsync($"/api/discord/guilds/{guildId}/suggestions");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToList();
    }

    private async Task WithDbAsync(Func<TrackerDbContext, Task> action)
    {
        using var scope = factory.Services.CreateScope();
        await action(scope.ServiceProvider.GetRequiredService<TrackerDbContext>());
    }

    private Task SeedUserAsync(int citizenId, string handle, string? displayName) => WithDbAsync(async db =>
    {
        var now = DateTime.UtcNow;
        db.Users.Add(new User { CitizenId = citizenId, UserHandle = handle, DisplayName = displayName, CreatedAt = now, UpdatedAt = now });
        await db.SaveChangesAsync();
    });

    private Task SeedHistoryAsync(int citizenId, string handle, DateTime lastSeen) => WithDbAsync(async db =>
    {
        db.UserHandleHistories.Add(new UserHandleHistory
        {
            CitizenId = citizenId, UserHandle = handle, FirstSeen = lastSeen.AddDays(-30), LastSeen = lastSeen,
        });
        await db.SaveChangesAsync();
    });

    private Task SeedOrgMemberAsync(string sid, string handle, int? citizenId, bool active) => WithDbAsync(async db =>
    {
        db.OrganizationMembers.Add(new OrganizationMember
        {
            OrgSid = sid, UserHandle = handle, CitizenId = citizenId, Timestamp = DateTime.UtcNow, IsActive = active,
        });
        await db.SaveChangesAsync();
    });

    private async Task<string> SeedGuildAsync(string? orgSid)
    {
        var guildId = DiscordTestKit.NewSnowflake();
        await WithDbAsync(async db =>
        {
            var now = DateTime.UtcNow;
            db.DiscordGuilds.Add(new DiscordGuild
            {
                GuildId = guildId, Name = $"Guild {guildId}", OrgSid = orgSid,
                FirstSyncAt = now, LastSyncAt = now, LastCollectedAt = now, LastCompleteSyncAt = now,
                CreatedAt = now, UpdatedAt = now,
            });
            await db.SaveChangesAsync();
        });
        return guildId;
    }

    private async Task<string> SeedMemberAsync(
        string guildId, string username, string? nick = null, bool bot = false, bool left = false)
    {
        var userId = DiscordTestKit.NewSnowflake();
        await WithDbAsync(async db =>
        {
            var now = DateTime.UtcNow;
            db.DiscordAccounts.Add(new DiscordAccount
            {
                DiscordUserId = userId, Username = username, IsBot = bot, FirstSeenAt = now, LastSeenAt = now,
            });
            db.DiscordMembers.Add(new DiscordMember
            {
                GuildId = guildId, DiscordUserId = userId, Nick = nick, RoleIdsJson = "[]",
                FirstSeenAt = now, LastSeenAt = now, LeftAt = left ? now : null,
            });
            await db.SaveChangesAsync();
        });
        return userId;
    }

    private Task SeedLinkAsync(string discordUserId) => WithDbAsync(async db =>
    {
        var now = DateTime.UtcNow;
        var entity = new TrackedEntity { CurrentHandle = $"linked-{discordUserId}", CreatedAt = now, UpdatedAt = now };
        db.TrackedEntities.Add(entity);
        await db.SaveChangesAsync();
        db.EntityLinks.Add(new EntityLink
        {
            TrackedEntityId = entity.Id, Provider = LinkProviders.Discord, Value = discordUserId,
            AuthorApiUserId = 0, AuthorUsername = "seed", CreatedAt = now, UpdatedAt = now,
        });
        await db.SaveChangesAsync();
    });

    private Task SeedRejectionAsync(string discordUserId, string citizenKey) => WithDbAsync(async db =>
    {
        db.DiscordLinkRejections.Add(new DiscordLinkRejection
        {
            DiscordUserId = discordUserId, CitizenKey = citizenKey, ByApiUserId = 0, ByUsername = "seed",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    });
}
```

- [ ] **Step 6: Write the failing link and rejection tests**

Create `src/Collector.Api.Tests/Discord/DiscordLinkTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Collector.Data;
using Collector.Models;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Collector.Api.Tests.Discord;

/// <summary>
/// POST api/discord/links, POST/DELETE api/discord/link-rejections (spec § 10.1), and the
/// snowflake rule of the generic links route. A link is made on the entity of the person
/// read back from the database, under their canonical current handle: a Discord spelling or
/// a former handle never reaches tracked_entities.CurrentHandle.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class DiscordLinkTests(ApiFactory factory)
{
    private static int _seq = 52_000;

    private static int Next() => Interlocked.Increment(ref _seq);

    /// <summary>A citizen id no other test uses.</summary>
    private static int Cid(int n, int k) => 1_520_000_000 + n * 10 + k;

    [Fact]
    public async Task AcceptingAFormerHandle_LinksTheCitizensEntity_AndKeepsItsCurrentHandle()
    {
        var n = Next();
        var cid = Cid(n, 0);
        await SeedUserAsync(cid, $"NewHandle{n}", "New display");
        await SeedHistoryAsync(cid, $"OldHandle{n}", DateTime.UtcNow.AddYears(-1));
        var entityId = await SeedEntityAsync(cid, $"NewHandle{n}", "New display");
        var userId = DiscordTestKit.NewSnowflake();
        var client = await factory.SignedInClientAsync($"c5-link-old-{n}");

        var first = await client.PostAsJsonAsync("/api/discord/links",
            new { discordUserId = userId, citizenId = (int?)null, handle = $"oldhandle{n}" });
        var again = await client.PostAsJsonAsync("/api/discord/links",
            new { discordUserId = userId, citizenId = cid, handle = $"oldhandle{n}" });

        first.StatusCode.Should().Be(HttpStatusCode.Created);
        again.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await first.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("entityId").GetInt64().Should().Be(entityId);
        body.GetProperty("handle").GetString().Should().Be($"NewHandle{n}");
        (await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("entityId").GetInt64().Should().Be(entityId);

        var entities = await ReadAsync(db => db.TrackedEntities.AsNoTracking().Where(e => e.CitizenId == cid).ToListAsync());
        entities.Should().ContainSingle().Which.CurrentHandle.Should().Be($"NewHandle{n}");
        var oldHandle = $"oldhandle{n}";
        (await ReadAsync(db => db.TrackedEntities.AnyAsync(e => e.CurrentHandle == oldHandle))).Should().BeFalse();
        (await ReadAsync(db => db.EntityLinks.CountAsync(l =>
                l.TrackedEntityId == entityId && l.Provider == LinkProviders.Discord && l.Value == userId)))
            .Should().Be(1, "linking twice is idempotent");
    }

    [Fact]
    public async Task ACaseMismatchedHandle_CreatesTheEntityUnderTheCanonicalHandle()
    {
        var n = Next();
        var cid = Cid(n, 0);
        await SeedUserAsync(cid, $"Pilote{n}", "Pilote affiché");
        var userId = DiscordTestKit.NewSnowflake();
        var client = await factory.SignedInClientAsync($"c5-link-case-{n}");

        var response = await client.PostAsJsonAsync("/api/discord/links",
            new { discordUserId = userId, citizenId = (int?)null, handle = $"pilote{n}" });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("handle").GetString().Should().Be($"Pilote{n}");
        var entity = (await ReadAsync(db => db.TrackedEntities.AsNoTracking().Where(e => e.CitizenId == cid).ToListAsync()))
            .Should().ContainSingle().Subject;
        entity.CurrentHandle.Should().Be($"Pilote{n}");
        (await ReadAsync(db => db.EntityLinks.CountAsync(l => l.TrackedEntityId == entity.Id && l.Value == userId)))
            .Should().Be(1);
    }

    [Fact]
    public async Task ARosterOnlyPerson_IsLinkedThroughTheirOrgMemberRow()
    {
        var n = Next();
        await SeedOrgMemberAsync($"LK{n}", $"Roster{n}", citizenId: null);
        var userId = DiscordTestKit.NewSnowflake();
        var client = await factory.SignedInClientAsync($"c5-link-roster-{n}");

        var response = await client.PostAsJsonAsync("/api/discord/links",
            new { discordUserId = userId, citizenId = (int?)null, handle = $"Roster{n}" });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("handle").GetString().Should().Be($"Roster{n}");
        var entityId = body.GetProperty("entityId").GetInt64();
        var entity = await ReadAsync(db => db.TrackedEntities.AsNoTracking().SingleAsync(e => e.Id == entityId));
        entity.CurrentHandle.Should().Be($"Roster{n}");
        entity.CitizenId.Should().BeNull();
    }

    [Fact]
    public async Task AnUnknownHandle_Returns404_AndCreatesNoEntity()
    {
        var n = Next();
        var handle = $"Nobody{n}";
        var client = await factory.SignedInClientAsync($"c5-link-404-{n}");

        var response = await client.PostAsJsonAsync("/api/discord/links",
            new { discordUserId = DiscordTestKit.NewSnowflake(), citizenId = (int?)null, handle });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadAsync(db => db.TrackedEntities.AnyAsync(e => e.CurrentHandle == handle))).Should().BeFalse();
    }

    [Fact]
    public async Task InvalidBodies_Return400()
    {
        var n = Next();
        var client = await factory.SignedInClientAsync($"c5-link-400-{n}");

        (await client.PostAsJsonAsync("/api/discord/links",
                new { discordUserId = "not-a-snowflake", citizenId = (int?)null, handle = $"Pilote{n}" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.PostAsJsonAsync("/api/discord/links",
                new { discordUserId = DiscordTestKit.NewSnowflake(), citizenId = (int?)null, handle = "   " }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.PostAsJsonAsync("/api/discord/link-rejections",
                new { discordUserId = "123", citizenId = (int?)null, handle = $"Pilote{n}" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ARejection_HidesTheSuggestion_UntilItIsUndone()
    {
        var n = Next();
        await SeedUserAsync(Cid(n, 0), $"Reject{n}", null);
        var guildId = await SeedGuildAsync();
        var userId = await SeedMemberAsync(guildId, $"reject{n}");
        var client = await factory.SignedInClientAsync($"c5-reject-{n}");
        var rejection = new { discordUserId = userId, citizenId = Cid(n, 0), handle = $"Reject{n}" };
        (await SuggestionCountAsync(client, guildId)).Should().Be(1);

        var created = await client.PostAsJsonAsync("/api/discord/link-rejections", rejection);
        var again = await client.PostAsJsonAsync("/api/discord/link-rejections", rejection);

        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
        (await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64().Should().Be(id);
        (await SuggestionCountAsync(client, guildId)).Should().Be(0);

        (await client.DeleteAsync($"/api/discord/link-rejections/{id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await SuggestionCountAsync(client, guildId)).Should().Be(1);
    }

    [Fact]
    public async Task OnlyItsAuthorOrAnAdmin_CanUndoARejection()
    {
        var n = Next();
        var author = await factory.SignedInClientAsync($"c5-rej-author-{n}");
        var other = await factory.SignedInClientAsync($"c5-rej-other-{n}");
        var admin = await factory.SignedInClientAsync($"c5-rej-admin-{n}", isAdmin: true);
        var created = await author.PostAsJsonAsync("/api/discord/link-rejections",
            new { discordUserId = DiscordTestKit.NewSnowflake(), citizenId = (int?)null, handle = $"Someone{n}" });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();

        (await other.DeleteAsync($"/api/discord/link-rejections/{id}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await admin.DeleteAsync($"/api/discord/link-rejections/{id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await admin.DeleteAsync($"/api/discord/link-rejections/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task TheGenericLinksRoute_RefusesADiscordValueThatIsNotASnowflake()
    {
        var n = Next();
        var client = await factory.SignedInClientAsync($"c5-links-route-{n}");
        var url = $"/api/users/c5linkroute{n}/links";

        (await client.PostAsJsonAsync(url, new { provider = "discord", value = "pilote#1234" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.PostAsJsonAsync(url, new { provider = "discord", value = "1234567890123456" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.PostAsJsonAsync(url, new { provider = "discord", value = DiscordTestKit.NewSnowflake() }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PostAsJsonAsync(url, new { provider = "uex", value = "not-a-number" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static async Task<int> SuggestionCountAsync(HttpClient client, string guildId)
    {
        var response = await client.GetAsync($"/api/discord/guilds/{guildId}/suggestions");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetArrayLength();
    }

    private async Task<T> ReadAsync<T>(Func<TrackerDbContext, Task<T>> read)
    {
        using var scope = factory.Services.CreateScope();
        return await read(scope.ServiceProvider.GetRequiredService<TrackerDbContext>());
    }

    private async Task WithDbAsync(Func<TrackerDbContext, Task> action)
    {
        using var scope = factory.Services.CreateScope();
        await action(scope.ServiceProvider.GetRequiredService<TrackerDbContext>());
    }

    private Task SeedUserAsync(int citizenId, string handle, string? displayName) => WithDbAsync(async db =>
    {
        var now = DateTime.UtcNow;
        db.Users.Add(new User { CitizenId = citizenId, UserHandle = handle, DisplayName = displayName, CreatedAt = now, UpdatedAt = now });
        await db.SaveChangesAsync();
    });

    private Task SeedHistoryAsync(int citizenId, string handle, DateTime lastSeen) => WithDbAsync(async db =>
    {
        db.UserHandleHistories.Add(new UserHandleHistory
        {
            CitizenId = citizenId, UserHandle = handle, FirstSeen = lastSeen.AddDays(-30), LastSeen = lastSeen,
        });
        await db.SaveChangesAsync();
    });

    private Task SeedOrgMemberAsync(string sid, string handle, int? citizenId) => WithDbAsync(async db =>
    {
        db.OrganizationMembers.Add(new OrganizationMember
        {
            OrgSid = sid, UserHandle = handle, CitizenId = citizenId, Timestamp = DateTime.UtcNow, IsActive = true,
        });
        await db.SaveChangesAsync();
    });

    private async Task<long> SeedEntityAsync(int citizenId, string handle, string? displayName)
    {
        long id = 0;
        await WithDbAsync(async db =>
        {
            var now = DateTime.UtcNow;
            var entity = new TrackedEntity
            {
                CitizenId = citizenId, CurrentHandle = handle, DisplayName = displayName,
                Source = TrackedEntitySource.Collected, Status = TrackedEntityStatus.Active,
                CreatedAt = now, UpdatedAt = now,
            };
            db.TrackedEntities.Add(entity);
            await db.SaveChangesAsync();
            id = entity.Id;
        });
        return id;
    }

    private async Task<string> SeedGuildAsync()
    {
        var guildId = DiscordTestKit.NewSnowflake();
        await WithDbAsync(async db =>
        {
            var now = DateTime.UtcNow;
            db.DiscordGuilds.Add(new DiscordGuild
            {
                GuildId = guildId, Name = $"Guild {guildId}",
                FirstSyncAt = now, LastSyncAt = now, LastCollectedAt = now, LastCompleteSyncAt = now,
                CreatedAt = now, UpdatedAt = now,
            });
            await db.SaveChangesAsync();
        });
        return guildId;
    }

    private async Task<string> SeedMemberAsync(string guildId, string username)
    {
        var userId = DiscordTestKit.NewSnowflake();
        await WithDbAsync(async db =>
        {
            var now = DateTime.UtcNow;
            db.DiscordAccounts.Add(new DiscordAccount
            {
                DiscordUserId = userId, Username = username, FirstSeenAt = now, LastSeenAt = now,
            });
            db.DiscordMembers.Add(new DiscordMember
            {
                GuildId = guildId, DiscordUserId = userId, RoleIdsJson = "[]", FirstSeenAt = now, LastSeenAt = now,
            });
            await db.SaveChangesAsync();
        });
        return userId;
    }
}
```

- [ ] **Step 7: Run the new API tests to verify they fail**

Run: `dotnet test src/Collector.Api.Tests --filter "FullyQualifiedName~DiscordSuggestionTests"`

Expected: 4 failures such as `Expected response.StatusCode to be HttpStatusCode.OK {value: 200}, but found HttpStatusCode.NotFound {value: 404}` (no route yet); `AnUnknownGuild_Returns404` already passes.

Run: `dotnet test src/Collector.Api.Tests --filter "FullyQualifiedName~DiscordLinkTests"`

Expected: 7 failures (the new routes answer 404; the generic links route answers `OK` instead of `BadRequest` for `pilote#1234`); `AnUnknownHandle_Returns404_AndCreatesNoEntity` already passes.

- [ ] **Step 8: Add the DTOs**

Append at the end of `src/Collector.Api/Dtos/Discord/DiscordRosterDtos.cs` (file-scoped namespace `Collector.Api.Dtos.Discord`, created in C2):

```csharp

/// <summary>How sure a link suggestion is (spec § 10.1).</summary>
public static class DiscordSuggestionConfidence
{
    /// <summary>The token equals the handle of an active member of the org the guild is mapped to.</summary>
    public const string Strong = "strong";

    /// <summary>The token equals a current or former RSI handle.</summary>
    public const string Medium = "medium";
}

/// <summary>A proposed link between an active Discord member and an RSI person, validated or ignored by hand.</summary>
public sealed class DiscordSuggestionDto
{
    public string DiscordUserId { get; set; } = null!;

    /// <summary>Nick, else global name, else username.</summary>
    public string DiscordName { get; set; } = null!;

    /// <summary>The name token that matched, as written on Discord.</summary>
    public string MatchedToken { get; set; } = null!;

    /// <summary>Canonical current RSI handle, read from the database (never the token).</summary>
    public string Handle { get; set; } = null!;

    public int? CitizenId { get; set; }
    public string? DisplayName { get; set; }

    /// <summary><see cref="DiscordSuggestionConfidence"/> value.</summary>
    public string Confidence { get; set; } = null!;
}

/// <summary>Body of POST api/discord/links: validate a suggestion.</summary>
public sealed record CreateDiscordLinkRequest(string DiscordUserId, int? CitizenId, string Handle);

/// <summary>Body of POST api/discord/link-rejections: ignore a suggestion.</summary>
public sealed record CreateDiscordLinkRejectionRequest(string DiscordUserId, int? CitizenId, string Handle);

/// <summary>Answer of POST api/discord/links: the linked entity and its canonical handle.</summary>
public sealed class DiscordLinkCreatedDto
{
    public long EntityId { get; set; }
    public string Handle { get; set; } = null!;
}

/// <summary>Answer of POST api/discord/link-rejections.</summary>
public sealed class DiscordLinkRejectionCreatedDto
{
    public long Id { get; set; }
}
```

- [ ] **Step 9: Write the suggestion service**

Create `src/Collector.Api/Services/Discord/DiscordSuggestionService.cs`:

```csharp
using System.Globalization;
using Collector.Api.Auth;
using Collector.Api.Dtos.Discord;
using Collector.Api.Errors;
using Collector.Data;
using Collector.Data.Repositories;
using Collector.Discord;
using Collector.Models;
using Collector.Services;
using Microsoft.EntityFrameworkCore;

namespace Collector.Api.Services.Discord;

/// <summary>
/// Link suggestions between Discord members and RSI people, and the two answers to them
/// (spec § 10.1). Suggestions are computed on read for the active, non-bot members of a
/// guild whose Discord id nobody is linked to yet. Validating one writes an entity link on
/// the person read back from the database; ignoring one writes a rejection keyed by
/// <see cref="CitizenKey"/> so the pair is not suggested again. Every write holds the Discord
/// write gate: the retention pass reads entity_links under it right before purging.
/// </summary>
public sealed class DiscordSuggestionService(
    TrackerDbContext db,
    DiscordHandleLookup lookup,
    IEntityResolver resolver,
    IEntityLinkRepository links,
    DiscordWriteGate gate,
    CurrentUserAccessor currentUser)
{
    /// <summary>Longest handle accepted, so that "h:" + handle fits the 100-character CitizenKey.</summary>
    public const int MaxHandleLength = 98;

    private static readonly TimeSpan GateTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Key of an RSI person in discord_link_rejections: the citizen id when known, else "h:"
    /// followed by the lower-case handle.
    /// </summary>
    public static string CitizenKey(int? citizenId, string handle)
        => citizenId is int cid ? cid.ToString(CultureInfo.InvariantCulture) : HandleKey(handle);

    /// <summary>The handle form of <see cref="CitizenKey"/>.</summary>
    public static string HandleKey(string handle) => "h:" + handle.Trim().ToLowerInvariant();

    /// <summary>
    /// Suggestions for the guild, strong first. Strong only exists for a guild mapped to an
    /// org; rejected pairs are left out, whichever key the rejection was saved under.
    /// </summary>
    public async Task<IReadOnlyList<DiscordSuggestionDto>> GetSuggestionsAsync(string guildId, CancellationToken ct)
    {
        var guild = await db.DiscordGuilds.AsNoTracking()
                .Where(g => g.GuildId == guildId)
                .Select(g => new { g.OrgSid })
                .FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException("Serveur Discord inconnu.");

        var members = await (
                from m in db.DiscordMembers.AsNoTracking()
                join a in db.DiscordAccounts.AsNoTracking() on m.DiscordUserId equals a.DiscordUserId
                where m.GuildId == guildId && m.LeftAt == null && !a.IsBot
                      && !db.EntityLinks.Any(l => l.Provider == LinkProviders.Discord && l.Value == m.DiscordUserId)
                select new { m.DiscordUserId, m.Nick, a.GlobalName, a.Username })
            .ToListAsync(ct);
        if (members.Count == 0) return [];

        var tokens = members.ToDictionary(
            m => m.DiscordUserId,
            m => HandleTokenizer.Candidates(m.Nick, m.GlobalName, m.Username),
            StringComparer.Ordinal);
        var matches = await lookup.FindAsync(tokens.Values.SelectMany(t => t), guild.OrgSid, ct);
        if (matches.Count == 0) return [];
        var matchesByValue = matches.ToLookup(m => m.MatchedValue, StringComparer.OrdinalIgnoreCase);

        var rejected = (await db.DiscordLinkRejections.AsNoTracking()
                .Where(r => db.DiscordMembers.Any(m =>
                    m.GuildId == guildId && m.LeftAt == null && m.DiscordUserId == r.DiscordUserId))
                .Select(r => new { r.DiscordUserId, r.CitizenKey })
                .ToListAsync(ct))
            .Select(r => (r.DiscordUserId, r.CitizenKey))
            .ToHashSet();

        var suggestions = new List<DiscordSuggestionDto>();
        foreach (var member in members)
        {
            // One suggestion per (member, person); the first token that matched is kept,
            // unless a later one makes it strong.
            var byPerson = new Dictionary<string, DiscordSuggestionDto>(StringComparer.Ordinal);
            foreach (var token in tokens[member.DiscordUserId])
            {
                foreach (var match in matchesByValue[token])
                {
                    var key = CitizenKey(match.CitizenId, match.Handle);
                    if (rejected.Contains((member.DiscordUserId, key))
                        || rejected.Contains((member.DiscordUserId, HandleKey(match.Handle))))
                        continue;

                    if (byPerson.TryGetValue(key, out var seen))
                    {
                        if (match.Strong && seen.Confidence != DiscordSuggestionConfidence.Strong)
                        {
                            seen.Confidence = DiscordSuggestionConfidence.Strong;
                            seen.MatchedToken = token;
                            seen.Handle = match.Handle;
                        }
                        continue;
                    }

                    byPerson[key] = new DiscordSuggestionDto
                    {
                        DiscordUserId = member.DiscordUserId,
                        DiscordName = member.Nick ?? member.GlobalName ?? member.Username,
                        MatchedToken = token,
                        Handle = match.Handle,
                        CitizenId = match.CitizenId,
                        DisplayName = match.DisplayName,
                        Confidence = match.Strong ? DiscordSuggestionConfidence.Strong : DiscordSuggestionConfidence.Medium,
                    };
                }
            }
            suggestions.AddRange(byPerson.Values);
        }

        return suggestions
            .OrderBy(s => s.Confidence == DiscordSuggestionConfidence.Strong ? 0 : 1)
            .ThenBy(s => s.DiscordName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Handle, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.DiscordUserId, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Validates a suggestion: re-reads the person under the write gate, resolves their
    /// entity from the citizen id and the canonical current handle (never the Discord token
    /// nor a former handle, which the resolver would write into CurrentHandle), then adds the
    /// discord link if it is not there yet. 404 when no source knows the handle.
    /// </summary>
    public async Task<DiscordLinkCreatedDto> LinkAsync(CreateDiscordLinkRequest request, CancellationToken ct)
    {
        var (discordUserId, handle) = Validate(request.DiscordUserId, request.CitizenId, request.Handle);
        using var lease = await EnterGateAsync(ct);

        var person = await lookup.ResolvePersonAsync(request.CitizenId, handle, ct)
            ?? throw new NotFoundException($"Aucun citoyen RSI connu sous le handle « {handle} ».");
        var entityId = await resolver.ResolveOrCreateAsync(person.CitizenId, person.Handle, person.DisplayName, ct);

        if (await links.GetByEntityProviderValueAsync(entityId, LinkProviders.Discord, discordUserId, ct) is null)
        {
            var now = DateTime.UtcNow;
            await links.AddAsync(new EntityLink
            {
                TrackedEntityId = entityId,
                Provider = LinkProviders.Discord,
                Value = discordUserId,
                AuthorApiUserId = currentUser.UserId ?? 0,
                AuthorUsername = currentUser.Username ?? "unknown",
                CreatedAt = now,
                UpdatedAt = now,
            }, ct);
            await links.SaveChangesAsync(ct);
        }

        return new DiscordLinkCreatedDto { EntityId = entityId, Handle = person.Handle };
    }

    /// <summary>Ignores a suggestion. Idempotent: the same pair returns the existing rejection's id.</summary>
    public async Task<long> RejectAsync(CreateDiscordLinkRejectionRequest request, CancellationToken ct)
    {
        var (discordUserId, handle) = Validate(request.DiscordUserId, request.CitizenId, request.Handle);
        var key = CitizenKey(request.CitizenId, handle);
        using var lease = await EnterGateAsync(ct);

        var existing = await db.DiscordLinkRejections.AsNoTracking()
            .Where(r => r.DiscordUserId == discordUserId && r.CitizenKey == key)
            .Select(r => (long?)r.Id)
            .FirstOrDefaultAsync(ct);
        if (existing is long id) return id;

        var row = new DiscordLinkRejection
        {
            DiscordUserId = discordUserId,
            CitizenKey = key,
            ByApiUserId = currentUser.UserId ?? 0,
            ByUsername = currentUser.Username ?? "unknown",
            CreatedAt = DateTime.UtcNow,
        };
        db.DiscordLinkRejections.Add(row);
        await db.SaveChangesAsync(ct);
        return row.Id;
    }

    /// <summary>Undoes a rejection: only its author or an admin may (403 otherwise).</summary>
    public async Task DeleteRejectionAsync(long id, CancellationToken ct)
    {
        using var lease = await EnterGateAsync(ct);
        var row = await db.DiscordLinkRejections.FirstOrDefaultAsync(r => r.Id == id, ct)
            ?? throw new NotFoundException("Rejet inconnu.");
        if (!currentUser.IsAdmin && row.ByApiUserId != (currentUser.UserId ?? -1))
            throw new ForbiddenException("Seuls l'auteur de ce rejet et les administrateurs peuvent l'annuler.");

        db.DiscordLinkRejections.Remove(row);
        await db.SaveChangesAsync(ct);
    }

    private static (string DiscordUserId, string Handle) Validate(string? discordUserId, int? citizenId, string? handle)
    {
        if (!DiscordSnowflake.IsValid(discordUserId))
            throw new ValidationException("discordUserId doit être un identifiant Discord (17 à 20 chiffres).");
        if (citizenId is <= 0)
            throw new ValidationException("citizenId doit être un nombre positif.");
        var trimmed = handle?.Trim() ?? "";
        if (trimmed.Length is 0 or > MaxHandleLength)
            throw new ValidationException($"handle doit faire de 1 à {MaxHandleLength} caractères.");
        return (discordUserId!, trimmed);
    }

    private async Task<IDisposable> EnterGateAsync(CancellationToken ct)
        => await gate.TryEnterAsync(GateTimeout, ct)
           ?? throw new ServiceUnavailableException("Écritures Discord en cours, réessaie dans 30 s.", 30);
}
```

- [ ] **Step 10: Make the controller partial and add the routes**

In `src/Collector.Api/Controllers/DiscordRostersController.cs` (created in C2), add the `partial` modifier to the class declaration (skip this edit if C2 already declared it `partial`). Replace:

```csharp
class DiscordRostersController
```

with:

```csharp
partial class DiscordRostersController
```

Create `src/Collector.Api/Controllers/DiscordRostersController.Links.cs`:

```csharp
using Collector.Api.Dtos.Discord;
using Collector.Api.Services.Discord;
using Microsoft.AspNetCore.Mvc;

namespace Collector.Api.Controllers;

/// <summary>
/// Link suggestions, links and rejections (spec § 10.1). The route prefix ("api"), the
/// [Authorize] requirement and the base class come from the main part of the controller.
/// </summary>
public partial class DiscordRostersController
{
    /// <summary>Suggested links for the guild's active, non-bot, unlinked members.</summary>
    [HttpGet("discord/guilds/{guildId}/suggestions")]
    public async Task<ActionResult<IReadOnlyList<DiscordSuggestionDto>>> GetLinkSuggestions(
        string guildId, [FromServices] DiscordSuggestionService suggestions, CancellationToken ct)
        => Ok(await suggestions.GetSuggestionsAsync(guildId, ct));

    /// <summary>Validates a suggestion: links the Discord id to the person's entity.</summary>
    [HttpPost("discord/links")]
    public async Task<ActionResult<DiscordLinkCreatedDto>> CreateDiscordLink(
        [FromBody] CreateDiscordLinkRequest request, [FromServices] DiscordSuggestionService suggestions,
        CancellationToken ct)
        => StatusCode(StatusCodes.Status201Created, await suggestions.LinkAsync(request, ct));

    /// <summary>Ignores a suggestion.</summary>
    [HttpPost("discord/link-rejections")]
    public async Task<ActionResult<DiscordLinkRejectionCreatedDto>> CreateLinkRejection(
        [FromBody] CreateDiscordLinkRejectionRequest request, [FromServices] DiscordSuggestionService suggestions,
        CancellationToken ct)
        => StatusCode(StatusCodes.Status201Created,
            new DiscordLinkRejectionCreatedDto { Id = await suggestions.RejectAsync(request, ct) });

    /// <summary>Undoes a rejection (its author or an admin).</summary>
    [HttpDelete("discord/link-rejections/{id:long}")]
    public async Task<IActionResult> DeleteLinkRejection(
        long id, [FromServices] DiscordSuggestionService suggestions, CancellationToken ct)
    {
        await suggestions.DeleteRejectionAsync(id, ct);
        return NoContent();
    }
}
```

- [ ] **Step 11: Register the services**

In `src/Collector.Api/Extensions/ServiceCollectionExtensions.cs`, method `AddApiServices`, replace:

```csharp
        services.AddHostedService<AudioOrphanSweeper>();
```

with:

```csharp
        services.AddHostedService<AudioOrphanSweeper>();

        // Discord link suggestions, links and rejections (spec § 10.1).
        services.AddScoped<Collector.Api.Services.Discord.DiscordHandleLookup>();
        services.AddScoped<Collector.Api.Services.Discord.DiscordSuggestionService>();
```

- [ ] **Step 12: Refuse non-snowflake Discord values on the generic links route**

In `src/Collector.Api/Controllers/LinksController.cs`, replace:

```csharp
using Collector.Api.Dtos.Links;
using Collector.Data.Repositories;
```

with:

```csharp
using Collector.Api.Dtos.Links;
using Collector.Data.Repositories;
using Collector.Discord;
```

and, in `CreateLink`, replace:

```csharp
        var value = req.Value.Trim();
        if (value.Length == 0) return BadRequest(new { message = "Valeur vide." });
```

with:

```csharp
        var value = req.Value.Trim();
        if (value.Length == 0) return BadRequest(new { message = "Valeur vide." });
        // A Discord link is a user id: new values must be snowflakes (older ones stay as they are).
        if (provider == LinkProviders.Discord && !DiscordSnowflake.IsValid(value))
            return BadRequest(new { message = "Identifiant Discord invalide : 17 à 20 chiffres." });
```

- [ ] **Step 13: Run the tests to verify they pass**

Run: `dotnet test src/Collector.Api.Tests --filter "FullyQualifiedName~DiscordSuggestionTests"`

Expected: `Passed!  - Failed:     0, Passed:     5, Skipped:     0, Total:     5`

Run: `dotnet test src/Collector.Api.Tests --filter "FullyQualifiedName~DiscordLinkTests"`

Expected: `Passed!  - Failed:     0, Passed:     8, Skipped:     0, Total:     8`

Run: `dotnet test src/Collector.Api.Tests --filter "FullyQualifiedName~DiscordHandleLookupTests"`

Expected: `Passed!  - Failed:     0, Passed:     3, Skipped:     0, Total:     3`

- [ ] **Step 14: Check that the new routes stay closed to anonymous callers and scoped keys**

Run: `dotnet test src/Collector.Api.Tests --filter "FullyQualifiedName~AuthorizationTests"`

Expected: `Passed!` with `Failed:     0` (the four new routes answer 401 to an anonymous caller and to a `discord:ingest` key).

- [ ] **Step 15: Commit**

```bash
git add src/Collector.Api/Services/Discord/DiscordHandleLookup.cs \
        src/Collector.Api/Services/Discord/DiscordSuggestionService.cs \
        src/Collector.Api/Controllers/DiscordRostersController.cs \
        src/Collector.Api/Controllers/DiscordRostersController.Links.cs \
        src/Collector.Api/Dtos/Discord/DiscordRosterDtos.cs \
        src/Collector.Api/Extensions/ServiceCollectionExtensions.cs \
        src/Collector.Api/Controllers/LinksController.cs \
        src/Collector.Api.Tests/Discord/DiscordHandleLookupTests.cs \
        src/Collector.Api.Tests/Discord/DiscordSuggestionTests.cs \
        src/Collector.Api.Tests/Discord/DiscordLinkTests.cs
git commit -F - <<'EOF'
feat(api): suggest discord links to rsi citizens and record links and rejections

Linking Discord members to RSI citizens by hand does not scale to a corpo
server. Suggestions match Discord name tokens against RSI handles regardless
of case, in batches of 500 served by the NOCASE indexes, and carry the
canonical current handle and citizen id read from the database. Accepting
one re-reads the person under the Discord write gate and resolves the
entity from that handle, so neither a Discord spelling nor a former handle
can overwrite CurrentHandle or fork a duplicate entity. Rejections keep an
ignored pair out of the list until their author or an admin undoes them.
The generic links route now refuses a Discord value that is not a
snowflake, so every new discord link can be joined to the rosters.

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
EOF
```

### Task C6: Guild configuration: corpo mapping and rank roles

**Files:**
- Create: `src/Collector.Api/Services/Discord/DiscordGuildConfigService.cs`
- Create: `src/Collector.Api/Controllers/DiscordRostersController.GuildConfig.cs`
- Modify: `src/Collector.Api/Dtos/Discord/DiscordRosterDtos.cs` (append at the end of the file)
- Modify: `src/Collector.Api/Extensions/ServiceCollectionExtensions.cs` (`AddApiServices`, after `services.AddHostedService<AudioOrphanSweeper>();`)
- Test: `src/Collector.Api.Tests/Discord/DiscordGuildConfigTests.cs`

**Interfaces:**
- Consumes:
  - CONTRACTS § 1: `DiscordGuild` (`OrgSid`, `OrgMappedByApiUserId`, `OrgMappedByUsername`, `OrgMappedAt`, `UpdatedAt`), `DiscordRole` (`IsRank`, `RankOrder`, `RsiRankLabel`, `Position`), DbSets `DiscordGuilds`, `DiscordRoles`; existing `Organizations`.
  - CONTRACTS § 5 (lot A): `DiscordWriteGate.TryEnterAsync(TimeSpan, CancellationToken)`, `DiscordAudit.LogAsync(ActivityLogService logs, CurrentUserAccessor user, ILogger logger, string action, string entityType, string entityId, CancellationToken ct)`, `ServiceUnavailableException(string, int, string? code = "busy")`; test kit `DiscordTestKit.IngestClientAsync`, `Sync`, `Member`, `Role`, `PostSyncAsync`, `NewSnowflake`.
  - Task C2: `DiscordRostersController` (made `partial` in C5); `DiscordRosterQueryService.GetGuildAsync(string guildId, CancellationToken ct) : Task<DiscordGuildDetailDto?>` whose `DiscordGuildDetailDto` carries every `DiscordGuildSummaryDto` field plus `Roles` (all roles, deleted ones included, as `DiscordRoleDto` with `RoleId`); `DiscordGuildSummaryDto`, `DiscordRoleDto`.
  - Task C3 (asserted through HTTP only): `GET api/discord/guilds/{guildId}/members` (`items[].reconciliation`), `GET api/discord/guilds/{guildId}/discrepancies` (`orgSid`, `items[].kind|handle|discordUserId`).
  - Task C4 (asserted through HTTP only): `GET api/organizations/{sid}/discord` (`[].guildId`).
- Produces:
  - `Collector.Api.Services.Discord.DiscordGuildConfigService` (scoped): `MapOrgAsync(string guildId, string? orgSid, CancellationToken)`, `UpdateRoleAsync(string guildId, string roleId, UpdateDiscordRoleRequest request, CancellationToken)`, `static bool CanEdit(DiscordGuild guild, long? userId, bool isAdmin)`, constants `MapOrgAction = "discord_map_org"`, `UpdateRoleAction = "discord_update_role"`, `GuildEntityType = "discord_guild"`, `RoleEntityType = "discord_role"`, `MaxRankOrder = 1000`, `MaxRsiRankLabelLength = 100`.
  - DTOs in `Collector.Api.Dtos.Discord`: `MapDiscordGuildOrgRequest(string? OrgSid)`, `UpdateDiscordRoleRequest(bool IsRank, int? RankOrder, string? RsiRankLabel)`.
  - Routes (CONTRACTS § 7): `PUT api/discord/guilds/{guildId}/org` (200 guild, 400 unknown SID, 403, 404), `PUT api/discord/guilds/{guildId}/roles/{roleId}` (200 `DiscordRoleDto`, 400, 403, 404).
  - activity_logs rows: action `discord_map_org`, entity type `discord_guild`, entity id `{guildId}`; action `discord_update_role`, entity type `discord_role`, entity id `{guildId}:{roleId}`.

- [ ] **Step 1: Write the failing tests**

Create `src/Collector.Api.Tests/Discord/DiscordGuildConfigTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Collector.Api.Data;
using Collector.Data;
using Collector.Models;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Collector.Api.Tests.Discord;

/// <summary>
/// PUT api/discord/guilds/{guildId}/org and PUT …/roles/{roleId} (spec § 11): anyone may
/// configure an unmapped guild; once mapped, only its responsible and admins may. Every edit
/// is audited, and a re-mapped guild is reconciled against its new org.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class DiscordGuildConfigTests(ApiFactory factory)
{
    private static int _seq = 62_000;

    private static int Next() => Interlocked.Increment(ref _seq);

    /// <summary>A citizen id no other test uses.</summary>
    private static int Cid(int n, int k) => 1_620_000_000 + n * 10 + k;

    private static string OrgUrl(string guildId) => $"/api/discord/guilds/{guildId}/org";

    private static string RoleUrl(string guildId, string roleId) => $"/api/discord/guilds/{guildId}/roles/{roleId}";

    [Fact]
    public async Task AnyUser_MapsAnUnmappedGuild_AndBecomesItsResponsible()
    {
        var n = Next();
        var sid = await SeedOrgAsync($"CFA{n}");
        var (guildId, _, _) = await IngestGuildAsync(n);
        var username = $"c6-map-{n}";
        var client = await factory.SignedInClientAsync(username);

        var response = await client.PutAsJsonAsync(OrgUrl(guildId), new { orgSid = $"  cfa{n} " });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("orgSid").GetString().Should().Be(sid);
        var guild = await GuildAsync(guildId);
        guild.OrgSid.Should().Be(sid);
        guild.OrgMappedByUsername.Should().Be(username);
        guild.OrgMappedByApiUserId.Should().Be(await ApiUserIdAsync(username));
        guild.OrgMappedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task TheResponsible_RemapsAndClears_WhileAnotherUserGets403()
    {
        var n = Next();
        var sidA = await SeedOrgAsync($"CFA{n}");
        var sidB = await SeedOrgAsync($"CFB{n}");
        var (guildId, roleId, _) = await IngestGuildAsync(n);
        var owner = await factory.SignedInClientAsync($"c6-owner-{n}");
        var other = await factory.SignedInClientAsync($"c6-other-{n}");
        (await owner.PutAsJsonAsync(OrgUrl(guildId), new { orgSid = sidA })).StatusCode.Should().Be(HttpStatusCode.OK);

        (await other.PutAsJsonAsync(OrgUrl(guildId), new { orgSid = sidB }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await other.PutAsJsonAsync(OrgUrl(guildId), new { orgSid = (string?)null }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await other.PutAsJsonAsync(RoleUrl(guildId, roleId), new { isRank = true, rankOrder = 1, rsiRankLabel = "X" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await GuildAsync(guildId)).OrgSid.Should().Be(sidA);
        (await RoleAsync(guildId, roleId)).IsRank.Should().BeFalse();

        (await owner.PutAsJsonAsync(OrgUrl(guildId), new { orgSid = sidB })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await GuildAsync(guildId)).OrgSid.Should().Be(sidB);

        (await owner.PutAsJsonAsync(OrgUrl(guildId), new { orgSid = (string?)null })).StatusCode.Should().Be(HttpStatusCode.OK);
        var cleared = await GuildAsync(guildId);
        cleared.OrgSid.Should().BeNull();
        cleared.OrgMappedByApiUserId.Should().BeNull();
        cleared.OrgMappedByUsername.Should().BeNull();
        cleared.OrgMappedAt.Should().BeNull();

        // Unmapped again: anyone may map it, and becomes its responsible.
        (await other.PutAsJsonAsync(OrgUrl(guildId), new { orgSid = sidA })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await GuildAsync(guildId)).OrgMappedByUsername.Should().Be($"c6-other-{n}");
    }

    [Fact]
    public async Task AnAdmin_OverridesTheResponsible()
    {
        var n = Next();
        var sidA = await SeedOrgAsync($"CFA{n}");
        var sidB = await SeedOrgAsync($"CFB{n}");
        var (guildId, roleId, _) = await IngestGuildAsync(n);
        var owner = await factory.SignedInClientAsync($"c6-owner2-{n}");
        var admin = await factory.SignedInClientAsync($"c6-admin-{n}", isAdmin: true);
        (await owner.PutAsJsonAsync(OrgUrl(guildId), new { orgSid = sidA })).StatusCode.Should().Be(HttpStatusCode.OK);

        (await admin.PutAsJsonAsync(OrgUrl(guildId), new { orgSid = sidB })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await admin.PutAsJsonAsync(RoleUrl(guildId, roleId), new { isRank = true, rankOrder = 2, rsiRankLabel = "Director" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var guild = await GuildAsync(guildId);
        guild.OrgSid.Should().Be(sidB);
        guild.OrgMappedByUsername.Should().Be($"c6-admin-{n}");
        (await RoleAsync(guildId, roleId)).RankOrder.Should().Be(2);
    }

    [Fact]
    public async Task AnUnknownSid_Returns400_AndLeavesTheGuildUnmapped()
    {
        var n = Next();
        var (guildId, _, _) = await IngestGuildAsync(n);
        var client = await factory.SignedInClientAsync($"c6-badsid-{n}");

        var response = await client.PutAsJsonAsync(OrgUrl(guildId), new { orgSid = $"NOPE{n}" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await GuildAsync(guildId)).OrgSid.Should().BeNull();
    }

    [Fact]
    public async Task AnUnknownGuildOrRole_Returns404()
    {
        var n = Next();
        var sid = await SeedOrgAsync($"CFA{n}");
        var (guildId, _, _) = await IngestGuildAsync(n);
        var client = await factory.SignedInClientAsync($"c6-404-{n}");
        var body = new { isRank = true, rankOrder = 1, rsiRankLabel = (string?)null };

        (await client.PutAsJsonAsync(OrgUrl(DiscordTestKit.NewSnowflake()), new { orgSid = sid }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.PutAsJsonAsync(RoleUrl(DiscordTestKit.NewSnowflake(), DiscordTestKit.NewSnowflake()), body))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.PutAsJsonAsync(RoleUrl(guildId, DiscordTestKit.NewSnowflake()), body))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ARoleBecomingARank_GetsItsPositionAsRankOrder()
    {
        var n = Next();
        var (guildId, roleId, _) = await IngestGuildAsync(n);
        var client = await factory.SignedInClientAsync($"c6-rank-{n}");

        var response = await client.PutAsJsonAsync(RoleUrl(guildId, roleId),
            new { isRank = true, rankOrder = (int?)null, rsiRankLabel = "  Officer  " });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var role = await response.Content.ReadFromJsonAsync<JsonElement>();
        role.GetProperty("roleId").GetString().Should().Be(roleId);
        role.GetProperty("isRank").GetBoolean().Should().BeTrue();
        role.GetProperty("rankOrder").GetInt32().Should().Be(7);
        role.GetProperty("rsiRankLabel").GetString().Should().Be("Officer");
        var stored = await RoleAsync(guildId, roleId);
        (stored.IsRank, stored.RankOrder, stored.RsiRankLabel).Should().Be((true, (int?)7, (string?)"Officer"));

        // An explicit order is kept, and stays when a later edit leaves it out.
        (await client.PutAsJsonAsync(RoleUrl(guildId, roleId), new { isRank = true, rankOrder = 3, rsiRankLabel = (string?)null }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PutAsJsonAsync(RoleUrl(guildId, roleId), new { isRank = true, rankOrder = (int?)null, rsiRankLabel = (string?)null }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        stored = await RoleAsync(guildId, roleId);
        (stored.IsRank, stored.RankOrder, stored.RsiRankLabel).Should().Be((true, (int?)3, (string?)null));

        (await client.PutAsJsonAsync(RoleUrl(guildId, roleId), new { isRank = true, rankOrder = -1, rsiRankLabel = (string?)null }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.PutAsJsonAsync(RoleUrl(guildId, roleId), new { isRank = true, rankOrder = 1, rsiRankLabel = new string('x', 101) }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Edits_AreWrittenToActivityLogs()
    {
        var n = Next();
        var sid = await SeedOrgAsync($"CFA{n}");
        var (guildId, roleId, _) = await IngestGuildAsync(n);
        var username = $"c6-audit-{n}";
        var client = await factory.SignedInClientAsync(username);

        (await client.PutAsJsonAsync(OrgUrl(guildId), new { orgSid = sid })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PutAsJsonAsync(RoleUrl(guildId, roleId), new { isRank = true, rankOrder = (int?)null, rsiRankLabel = (string?)null }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var userId = await ApiUserIdAsync(username);
        var roleEntityId = $"{guildId}:{roleId}";
        using var scope = factory.Services.CreateScope();
        var logs = await scope.ServiceProvider.GetRequiredService<ApiDbContext>().ActivityLogs.AsNoTracking()
            .Where(l => l.EntityId == guildId || l.EntityId == roleEntityId)
            .Select(l => new { l.Action, l.EntityType, l.EntityId, l.ApiUserId })
            .ToListAsync();
        logs.Should().ContainEquivalentOf(new
        {
            Action = "discord_map_org", EntityType = (string?)"discord_guild", EntityId = (string?)guildId, ApiUserId = (long?)userId,
        });
        logs.Should().ContainEquivalentOf(new
        {
            Action = "discord_update_role", EntityType = (string?)"discord_role", EntityId = (string?)roleEntityId, ApiUserId = (long?)userId,
        });
    }

    [Fact]
    public async Task ARemappedGuild_IsReconciledAgainstItsNewOrg()
    {
        var n = Next();
        var handleA = $"RemapA{n}";
        var handleB = $"RemapB{n}";
        var sidA = await SeedOrgAsync($"CFA{n}", (handleA, Cid(n, 1)));
        var sidB = await SeedOrgAsync($"CFB{n}", (handleB, Cid(n, 2)));
        var (guildId, _, userId) = await IngestGuildAsync(n);
        await SeedLinkAsync(userId, Cid(n, 1), handleA);
        var client = await factory.SignedInClientAsync($"c6-remap-{n}");

        (await client.PutAsJsonAsync(OrgUrl(guildId), new { orgSid = sidA })).StatusCode.Should().Be(HttpStatusCode.OK);

        (await OrgGuildIdsAsync(client, sidA)).Should().Contain(guildId);
        (await OrgGuildIdsAsync(client, sidB)).Should().NotContain(guildId);
        (await ReconciliationAsync(client, guildId, userId)).Should().Be("ok");
        var before = await DiscrepanciesAsync(client, guildId);
        before.GetProperty("orgSid").GetString().Should().Be(sidA);
        Items(before).Should().NotContain(i => i.GetProperty("discordUserId").GetString() == userId);

        (await client.PutAsJsonAsync(OrgUrl(guildId), new { orgSid = sidB })).StatusCode.Should().Be(HttpStatusCode.OK);

        (await OrgGuildIdsAsync(client, sidA)).Should().NotContain(guildId);
        (await OrgGuildIdsAsync(client, sidB)).Should().Contain(guildId);
        (await ReconciliationAsync(client, guildId, userId)).Should().Be("not_in_rsi_org");
        var after = await DiscrepanciesAsync(client, guildId);
        after.GetProperty("orgSid").GetString().Should().Be(sidB);
        Items(after).Should().Contain(i =>
            Kind(i) == "not_in_rsi_org" && i.GetProperty("discordUserId").GetString() == userId);
        Items(after).Should().Contain(i => Kind(i) == "rsi_only" && i.GetProperty("handle").GetString() == handleB);
        Items(after).Should().NotContain(i => Kind(i) == "rsi_only" && i.GetProperty("handle").GetString() == handleA);
    }

    private static IEnumerable<JsonElement> Items(JsonElement discrepancies)
        => discrepancies.GetProperty("items").EnumerateArray().ToList();

    private static string? Kind(JsonElement item) => item.GetProperty("kind").GetString();

    private static async Task<List<string?>> OrgGuildIdsAsync(HttpClient client, string sid)
        => (await client.GetFromJsonAsync<JsonElement>($"/api/organizations/{sid}/discord"))
            .EnumerateArray().Select(g => g.GetProperty("guildId").GetString()).ToList();

    private static async Task<string?> ReconciliationAsync(HttpClient client, string guildId, string userId)
    {
        var page = await client.GetFromJsonAsync<JsonElement>(
            $"/api/discord/guilds/{guildId}/members?status=active&page=1&pageSize=50");
        return page.GetProperty("items").EnumerateArray()
            .Single(m => m.GetProperty("discordUserId").GetString() == userId)
            .GetProperty("reconciliation").GetString();
    }

    private static Task<JsonElement> DiscrepanciesAsync(HttpClient client, string guildId)
        => client.GetFromJsonAsync<JsonElement>($"/api/discord/guilds/{guildId}/discrepancies");

    /// <summary>A guild received through the real ingest: one member holding one non-hoisted role at position 7.</summary>
    private async Task<(string GuildId, string RoleId, string UserId)> IngestGuildAsync(int n)
    {
        var (ingest, _, _) = await DiscordTestKit.IngestClientAsync(factory, $"c6-ingest-{n}");
        var guildId = DiscordTestKit.NewSnowflake();
        var roleId = DiscordTestKit.NewSnowflake();
        var userId = DiscordTestKit.NewSnowflake();
        var sync = DiscordTestKit.Sync(guildId,
            [DiscordTestKit.Member(userId, $"c6member{n}", roleIds: [roleId])],
            roles: [DiscordTestKit.Role(roleId, "Officier", 7, hoist: false)]);
        (await DiscordTestKit.PostSyncAsync(ingest, guildId, sync)).StatusCode.Should().Be(HttpStatusCode.OK);
        return (guildId, roleId, userId);
    }

    private async Task<string> SeedOrgAsync(string sid, params (string Handle, int CitizenId)[] roster)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TrackerDbContext>();
        var now = DateTime.UtcNow;
        db.Organizations.Add(new Organization { Sid = sid, Name = $"Org {sid}", Timestamp = now });
        foreach (var (handle, citizenId) in roster)
            db.OrganizationMembers.Add(new OrganizationMember
            {
                OrgSid = sid, UserHandle = handle, CitizenId = citizenId, Timestamp = now, IsActive = true,
            });
        await db.SaveChangesAsync();
        return sid;
    }

    private async Task SeedLinkAsync(string discordUserId, int citizenId, string handle)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TrackerDbContext>();
        var now = DateTime.UtcNow;
        var entity = new TrackedEntity { CitizenId = citizenId, CurrentHandle = handle, CreatedAt = now, UpdatedAt = now };
        db.TrackedEntities.Add(entity);
        await db.SaveChangesAsync();
        db.EntityLinks.Add(new EntityLink
        {
            TrackedEntityId = entity.Id, Provider = LinkProviders.Discord, Value = discordUserId,
            AuthorApiUserId = 0, AuthorUsername = "seed", CreatedAt = now, UpdatedAt = now,
        });
        await db.SaveChangesAsync();
    }

    private async Task<DiscordGuild> GuildAsync(string guildId)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<TrackerDbContext>()
            .DiscordGuilds.AsNoTracking().SingleAsync(g => g.GuildId == guildId);
    }

    private async Task<DiscordRole> RoleAsync(string guildId, string roleId)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<TrackerDbContext>()
            .DiscordRoles.AsNoTracking().SingleAsync(r => r.GuildId == guildId && r.RoleId == roleId);
    }

    private async Task<long> ApiUserIdAsync(string username)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ApiDbContext>()
            .ApiUsers.Where(u => u.Username == username).Select(u => u.Id).SingleAsync();
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test src/Collector.Api.Tests --filter "FullyQualifiedName~DiscordGuildConfigTests"`

Expected: 7 failures such as `Expected response.StatusCode to be HttpStatusCode.OK {value: 200}, but found HttpStatusCode.NotFound {value: 404}` (the PUT routes do not exist yet); `AnUnknownGuildOrRole_Returns404` already passes.

- [ ] **Step 3: Add the request DTOs**

Append at the end of `src/Collector.Api/Dtos/Discord/DiscordRosterDtos.cs`:

```csharp

/// <summary>Body of PUT api/discord/guilds/{guildId}/org: an RSI SID, or null to unmap the guild.</summary>
public sealed record MapDiscordGuildOrgRequest(string? OrgSid);

/// <summary>Body of PUT api/discord/guilds/{guildId}/roles/{roleId}: rank configuration of one role.</summary>
public sealed record UpdateDiscordRoleRequest(bool IsRank, int? RankOrder, string? RsiRankLabel);
```

- [ ] **Step 4: Write the configuration service**

Create `src/Collector.Api/Services/Discord/DiscordGuildConfigService.cs`:

```csharp
using Collector.Api.Auth;
using Collector.Api.Dtos.Discord;
using Collector.Api.Errors;
using Collector.Data;
using Collector.Models;
using Microsoft.EntityFrameworkCore;

namespace Collector.Api.Services.Discord;

/// <summary>
/// Site-side configuration of a guild (spec § 11): the corpo it belongs to, and which of its
/// roles are ranks. An unmapped guild may be configured by any signed-in user; once mapped,
/// only the user who mapped it (its responsible) and admins may change the corpo or the
/// roles, as for citizen ids and manual memberships. Writes hold the Discord write gate and
/// are recorded in activity_logs once saved.
/// </summary>
public sealed class DiscordGuildConfigService(
    TrackerDbContext db,
    DiscordWriteGate gate,
    CurrentUserAccessor currentUser,
    ActivityLogService activityLog,
    ILogger<DiscordGuildConfigService> logger)
{
    public const string MapOrgAction = "discord_map_org";
    public const string UpdateRoleAction = "discord_update_role";
    public const string GuildEntityType = "discord_guild";
    public const string RoleEntityType = "discord_role";

    /// <summary>Upper bound of a rank order, as for role positions.</summary>
    public const int MaxRankOrder = 1000;

    public const int MaxRsiRankLabelLength = 100;

    private static readonly TimeSpan GateTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The responsible-user rule: anyone may configure an unmapped guild; once mapped, only
    /// the user who mapped it and admins may.
    /// </summary>
    public static bool CanEdit(DiscordGuild guild, long? userId, bool isAdmin)
        => guild.OrgSid is null || isAdmin || (guild.OrgMappedByApiUserId is long owner && owner == userId);

    /// <summary>
    /// Maps the guild to <paramref name="orgSid"/> (upper-cased, must exist in organizations,
    /// else 400) and makes the caller its responsible; null unmaps it and clears the
    /// responsible. 404 for an unknown guild, 403 when the caller may not edit it.
    /// </summary>
    public async Task MapOrgAsync(string guildId, string? orgSid, CancellationToken ct)
    {
        using (await EnterGateAsync(ct))
        {
            var guild = await db.DiscordGuilds.FirstOrDefaultAsync(g => g.GuildId == guildId, ct)
                ?? throw new NotFoundException("Serveur Discord inconnu.");
            EnsureCanEdit(guild);

            var sid = orgSid?.Trim().ToUpperInvariant();
            if (sid is not null && (sid.Length == 0 || !await db.Organizations.AnyAsync(o => o.Sid == sid, ct)))
                throw new ValidationException($"Corpo inconnue : « {orgSid!.Trim()} ». Choisis un SID connu du tracker.");

            var now = DateTime.UtcNow;
            guild.OrgSid = sid;
            guild.OrgMappedByApiUserId = sid is null ? null : currentUser.UserId;
            guild.OrgMappedByUsername = sid is null ? null : currentUser.Username ?? "unknown";
            guild.OrgMappedAt = sid is null ? null : now;
            guild.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
        }

        await DiscordAudit.LogAsync(activityLog, currentUser, logger, MapOrgAction, GuildEntityType, guildId, ct);
    }

    /// <summary>
    /// Sets whether the role is a rank, its order and its RSI equivalent. A role that becomes
    /// a rank without an explicit order takes its Discord position, so every rank has an
    /// order; a rank edited without an order keeps its own. 404 for an unknown guild or role,
    /// 403 when the caller may not edit the guild, 400 for an invalid body.
    /// </summary>
    public async Task UpdateRoleAsync(
        string guildId, string roleId, UpdateDiscordRoleRequest request, CancellationToken ct)
    {
        if (request.RankOrder is < 0 or > MaxRankOrder)
            throw new ValidationException($"rankOrder doit être compris entre 0 et {MaxRankOrder}.");
        var label = string.IsNullOrWhiteSpace(request.RsiRankLabel) ? null : request.RsiRankLabel.Trim();
        if (label is { Length: > MaxRsiRankLabelLength })
            throw new ValidationException($"rsiRankLabel fait {MaxRsiRankLabelLength} caractères au plus.");

        using (await EnterGateAsync(ct))
        {
            var guild = await db.DiscordGuilds.AsNoTracking().FirstOrDefaultAsync(g => g.GuildId == guildId, ct)
                ?? throw new NotFoundException("Serveur Discord inconnu.");
            EnsureCanEdit(guild);
            var role = await db.DiscordRoles.FirstOrDefaultAsync(r => r.GuildId == guildId && r.RoleId == roleId, ct)
                ?? throw new NotFoundException("Rôle Discord inconnu.");

            role.RankOrder = request.IsRank
                ? request.RankOrder ?? (role.IsRank ? role.RankOrder : null) ?? role.Position
                : request.RankOrder;
            role.IsRank = request.IsRank;
            role.RsiRankLabel = label;
            await db.SaveChangesAsync(ct);
        }

        await DiscordAudit.LogAsync(activityLog, currentUser, logger, UpdateRoleAction, RoleEntityType,
            $"{guildId}:{roleId}", ct);
    }

    private void EnsureCanEdit(DiscordGuild guild)
    {
        if (!CanEdit(guild, currentUser.UserId, currentUser.IsAdmin))
            throw new ForbiddenException(
                $"Ce serveur est relié par {guild.OrgMappedByUsername ?? "un administrateur"} : seuls ce responsable et les administrateurs peuvent le modifier.");
    }

    private async Task<IDisposable> EnterGateAsync(CancellationToken ct)
        => await gate.TryEnterAsync(GateTimeout, ct)
           ?? throw new ServiceUnavailableException("Écritures Discord en cours, réessaie dans 30 s.", 30);
}
```

- [ ] **Step 5: Add the routes**

Create `src/Collector.Api/Controllers/DiscordRostersController.GuildConfig.cs`:

```csharp
using Collector.Api.Dtos.Discord;
using Collector.Api.Services.Discord;
using Microsoft.AspNetCore.Mvc;

namespace Collector.Api.Controllers;

/// <summary>
/// Guild configuration: corpo mapping and rank roles (spec § 11). Both answers are read back
/// through <see cref="DiscordRosterQueryService"/>, so they match what the GET routes return.
/// </summary>
public partial class DiscordRostersController
{
    /// <summary>Maps the guild to an org (or unmaps it with null) and returns the guild.</summary>
    [HttpPut("discord/guilds/{guildId}/org")]
    public async Task<ActionResult<DiscordGuildSummaryDto>> MapGuildOrg(
        string guildId, [FromBody] MapDiscordGuildOrgRequest request,
        [FromServices] DiscordGuildConfigService config, [FromServices] DiscordRosterQueryService queries,
        CancellationToken ct)
    {
        await config.MapOrgAsync(guildId, request.OrgSid, ct);
        var guild = await queries.GetGuildAsync(guildId, ct);
        if (guild is null) return NotFound();
        return Ok(guild);
    }

    /// <summary>Configures one role as a rank (or not) and returns it.</summary>
    [HttpPut("discord/guilds/{guildId}/roles/{roleId}")]
    public async Task<ActionResult<DiscordRoleDto>> UpdateGuildRole(
        string guildId, string roleId, [FromBody] UpdateDiscordRoleRequest request,
        [FromServices] DiscordGuildConfigService config, [FromServices] DiscordRosterQueryService queries,
        CancellationToken ct)
    {
        await config.UpdateRoleAsync(guildId, roleId, request, ct);
        var guild = await queries.GetGuildAsync(guildId, ct);
        var role = guild?.Roles.FirstOrDefault(r => r.RoleId == roleId);
        if (role is null) return NotFound();
        return Ok(role);
    }
}
```

- [ ] **Step 6: Register the service**

In `src/Collector.Api/Extensions/ServiceCollectionExtensions.cs`, method `AddApiServices`, replace:

```csharp
        services.AddHostedService<AudioOrphanSweeper>();
```

with:

```csharp
        services.AddHostedService<AudioOrphanSweeper>();

        // Discord guild configuration: corpo mapping and rank roles (spec § 11).
        services.AddScoped<Collector.Api.Services.Discord.DiscordGuildConfigService>();
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test src/Collector.Api.Tests --filter "FullyQualifiedName~DiscordGuildConfigTests"`

Expected: `Passed!  - Failed:     0, Passed:     8, Skipped:     0, Total:     8`

- [ ] **Step 8: Check that the new routes stay closed to anonymous callers and scoped keys**

Run: `dotnet test src/Collector.Api.Tests --filter "FullyQualifiedName~AuthorizationTests"`

Expected: `Passed!` with `Failed:     0`.

- [ ] **Step 9: Commit**

```bash
git add src/Collector.Api/Services/Discord/DiscordGuildConfigService.cs \
        src/Collector.Api/Controllers/DiscordRostersController.GuildConfig.cs \
        src/Collector.Api/Dtos/Discord/DiscordRosterDtos.cs \
        src/Collector.Api/Extensions/ServiceCollectionExtensions.cs \
        src/Collector.Api.Tests/Discord/DiscordGuildConfigTests.cs
git commit -F - <<'EOF'
feat(api): map discord guilds to corpos and configure their rank roles

Reconciliation needs to know which corpo a server belongs to and which of
its roles are ranks, and only the site can say it: the plugin never sends
either. The first user to map a server becomes its responsible; after that
only they and admins may change the corpo or the ranks, as for citizen ids
and manual memberships, and every change is written to activity_logs. A
role that becomes a rank takes its Discord position as order, so every rank
can be sorted. Statuses are computed on read, so a server re-mapped from one
corpo to another is reconciled against the new roster at once.

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
EOF
```

### Task C7: Retention service

**Files:**
- Create: `src/Collector/Data/Repositories/IDiscordRetentionRepository.cs`
- Create: `src/Collector/Data/Repositories/DiscordRetentionRepository.cs`
- Modify: `src/Collector/Extensions/ServiceCollectionExtensions.cs` (`AddCollectorDataServices`, after `services.AddScoped<IEntityLinkRepository, EntityLinkRepository>();`)
- Create: `src/Collector.Api/Services/Discord/DiscordRetentionService.cs`
- Modify: `src/Collector.Api/Extensions/ServiceCollectionExtensions.cs` (`AddApiServices`, after `services.AddHostedService<AudioOrphanSweeper>();`)
- Modify: `src/Collector.Api.Tests/Collector.Api.Tests.csproj` (package references) and `src/Collector.Api.Tests/packages.lock.json` (regenerated by restore)
- Test: `src/Collector.Tests/Discord/DiscordRetentionRepositoryTests.cs`
- Test: `src/Collector.Api.Tests/Discord/DiscordRetentionServiceTests.cs`

**Interfaces:**
- Consumes:
  - CONTRACTS § 1: DbSets `DiscordSyncs`, `DiscordAccounts`, `DiscordMembers`, `DiscordMemberEvents`, `DiscordLinkRejections`; entities `DiscordSync`, `DiscordAccount`, `DiscordMember`, `DiscordMemberEvent`, `DiscordLinkRejection`, `DiscordSyncMethods`, `DiscordEventTypes`; existing `EntityLinks`, `LinkProviders.Discord`.
  - CONTRACTS § 4 (lot A): `Collector.Api.Options.DiscordOptions.Retention { SyncLogDays = 365, DepartedAccountDays = 730 }`, bound once by lot A in `AddApiServices` (C7 does not bind it again).
  - CONTRACTS § 5 (lot A): `DiscordWriteGate.EnterAsync(CancellationToken ct) : Task<IDisposable>` (singleton).
  - Existing: `AddCollectorDataServices`, `EnsureDatabaseAsync` (`Collector.Extensions`), `ApiFactory`, `ApiCollection`; package `Microsoft.Extensions.TimeProvider.Testing` (version pinned in `Directory.Packages.props`).
- Produces:
  - `Collector.Data.Repositories.IDiscordRetentionRepository` (scoped, registered in `AddCollectorDataServices`): `Task<int> PurgeSyncLogsAsync(DateTime before, int batchSize, CancellationToken ct = default)`, `Task<int> PurgeDepartedAccountsAsync(DateTime before, int batchSize, CancellationToken ct = default)`; implementation `DiscordRetentionRepository`.
  - `Collector.Api.Services.Discord.DiscordRetentionService : BackgroundService` (hosted): `DefaultBatchSize = 500`, init properties `StartupDelay` (10 min), `Interval` (1 day), `BatchSize`; `Task<DiscordRetentionResult> RunOnceAsync(CancellationToken ct)`; record `DiscordRetentionResult(int SyncLogsDeleted, int AccountsPurged)`.
  - `TimeProvider` registered in the API (`TimeProvider.System`) when nothing registered it before.

- [ ] **Step 1: Write the failing repository tests**

Create `src/Collector.Tests/Discord/DiscordRetentionRepositoryTests.cs`:

```csharp
using Collector.Data;
using Collector.Data.Repositories;
using Collector.Models;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Collector.Tests.Discord;

/// <summary>
/// Retention purges (spec § 13.3), one batch per call: sync log rows received before the
/// cutoff, and unlinked accounts whose every member row is older than it (or that have no
/// member row and were last seen before it), with their members, events and rejections.
/// </summary>
public sealed class DiscordRetentionRepositoryTests : IAsyncLifetime
{
    private static readonly DateTime Cutoff = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Old = Cutoff.AddDays(-10);
    private static readonly DateTime Recent = Cutoff.AddDays(10);
    private const string G1 = "200000000000000001";
    private const string G2 = "200000000000000002";
    private const string A = "100000000000000001";
    private const string B = "100000000000000002";

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private TrackerDbContext _db = null!;

    public async Task InitializeAsync()
    {
        _connection.Open();
        _db = new TrackerDbContext(new DbContextOptionsBuilder<TrackerDbContext>().UseSqlite(_connection).Options);
        await _db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        _connection.Dispose();
    }

    private DiscordRetentionRepository Repo() => new(_db);

    [Fact]
    public async Task SyncLogs_ReceivedBeforeTheCutoff_AreDeleted_NewerOnesKept()
    {
        await SeedSyncsAsync(Old, Cutoff.AddSeconds(-1), Cutoff, Recent);

        var deleted = await Repo().PurgeSyncLogsAsync(Cutoff, 500);

        deleted.Should().Be(2);
        (await _db.DiscordSyncs.AsNoTracking().Select(s => s.ReceivedAt).ToListAsync())
            .Should().BeEquivalentTo(new[] { Cutoff, Recent });
    }

    [Fact]
    public async Task SyncLogs_AreDeletedOneBatchAtATime()
    {
        await SeedSyncsAsync(Old, Old, Old, Old, Old);
        var repo = Repo();

        (await repo.PurgeSyncLogsAsync(Cutoff, 2)).Should().Be(2);
        (await repo.PurgeSyncLogsAsync(Cutoff, 2)).Should().Be(2);
        (await repo.PurgeSyncLogsAsync(Cutoff, 2)).Should().Be(1);
        (await repo.PurgeSyncLogsAsync(Cutoff, 2)).Should().Be(0);
    }

    [Fact]
    public async Task AnUnlinkedAccount_GoneFromEveryGuild_IsPurged_WithItsMembersEventsAndRejections()
    {
        await SeedAccountAsync(A, Old);
        await SeedMemberAsync(G1, A, lastSeen: Old.AddDays(-30), leftAt: Old);
        await SeedMemberAsync(G2, A, lastSeen: Old, leftAt: null);
        await SeedEventAsync(G1, A);
        await SeedEventAsync(null, A);
        await SeedRejectionAsync(A);
        // A bystander still present keeps every row.
        await SeedAccountAsync(B, Recent);
        await SeedMemberAsync(G1, B, lastSeen: Recent, leftAt: null);
        await SeedEventAsync(G1, B);
        await SeedRejectionAsync(B);

        (await Repo().PurgeDepartedAccountsAsync(Cutoff, 500)).Should().Be(1);

        (await RowsOfAsync(A)).Should().Be((0, 0, 0, 0));
        (await RowsOfAsync(B)).Should().Be((1, 1, 1, 1));
    }

    [Fact]
    public async Task ALinkedAccount_IsKept()
    {
        await SeedAccountAsync(A, Old);
        await SeedMemberAsync(G1, A, lastSeen: Old, leftAt: Old);
        await SeedEventAsync(G1, A);
        await SeedLinkAsync(A);

        (await Repo().PurgeDepartedAccountsAsync(Cutoff, 500)).Should().Be(0);

        (await RowsOfAsync(A)).Should().Be((1, 1, 1, 0));
    }

    [Fact]
    public async Task AnAccountWithoutMemberRows_IsPurgedOnlyOnceItsLastSightingIsOld()
    {
        await SeedAccountAsync(A, Old);
        await SeedAccountAsync(B, Recent);

        (await Repo().PurgeDepartedAccountsAsync(Cutoff, 500)).Should().Be(1);

        (await RowsOfAsync(A)).Accounts.Should().Be(0);
        (await RowsOfAsync(B)).Accounts.Should().Be(1);
    }

    [Fact]
    public async Task AnAccountStillActiveInOneGuild_IsKept()
    {
        await SeedAccountAsync(A, Recent);
        await SeedMemberAsync(G1, A, lastSeen: Old, leftAt: Old);
        await SeedMemberAsync(G2, A, lastSeen: Recent, leftAt: null);

        (await Repo().PurgeDepartedAccountsAsync(Cutoff, 500)).Should().Be(0);

        (await RowsOfAsync(A)).Should().Be((1, 2, 0, 0));
    }

    [Fact]
    public async Task ARecentDeparture_KeepsTheAccount_EvenWhenItsLastSightingIsOld()
    {
        // coalesce(LeftAt, LastSeenAt): the departure date counts, not the last sighting.
        await SeedAccountAsync(A, Old);
        await SeedMemberAsync(G1, A, lastSeen: Old, leftAt: Recent);

        (await Repo().PurgeDepartedAccountsAsync(Cutoff, 500)).Should().Be(0);

        (await RowsOfAsync(A)).Accounts.Should().Be(1);
    }

    [Fact]
    public async Task DepartedAccounts_ArePurgedOneBatchAtATime()
    {
        for (var i = 1; i <= 5; i++) await SeedAccountAsync($"10000000000000010{i}", Old);
        var repo = Repo();

        (await repo.PurgeDepartedAccountsAsync(Cutoff, 2)).Should().Be(2);
        (await repo.PurgeDepartedAccountsAsync(Cutoff, 2)).Should().Be(2);
        (await repo.PurgeDepartedAccountsAsync(Cutoff, 2)).Should().Be(1);
        (await repo.PurgeDepartedAccountsAsync(Cutoff, 2)).Should().Be(0);
    }

    private async Task<(int Accounts, int Members, int Events, int Rejections)> RowsOfAsync(string userId) => (
        await _db.DiscordAccounts.CountAsync(a => a.DiscordUserId == userId),
        await _db.DiscordMembers.CountAsync(m => m.DiscordUserId == userId),
        await _db.DiscordMemberEvents.CountAsync(e => e.DiscordUserId == userId),
        await _db.DiscordLinkRejections.CountAsync(r => r.DiscordUserId == userId));

    private async Task SeedSyncsAsync(params DateTime[] receivedAt)
    {
        foreach (var at in receivedAt)
            _db.DiscordSyncs.Add(new DiscordSync
            {
                GuildId = G1, SubmittedByApiUserId = 1, SubmittedByUsername = "sender",
                ReceivedAt = at, CollectedAt = at, DeclaredCollectedAt = at,
                Method = DiscordSyncMethods.MemberSearch, PluginVersion = "1.0.0",
            });
        await SaveAsync();
    }

    private async Task SeedAccountAsync(string userId, DateTime lastSeen)
    {
        _db.DiscordAccounts.Add(new DiscordAccount
        {
            DiscordUserId = userId, Username = $"user{userId[^4..]}", FirstSeenAt = lastSeen.AddDays(-100), LastSeenAt = lastSeen,
        });
        await SaveAsync();
    }

    private async Task SeedMemberAsync(string guildId, string userId, DateTime lastSeen, DateTime? leftAt)
    {
        _db.DiscordMembers.Add(new DiscordMember
        {
            GuildId = guildId, DiscordUserId = userId, RoleIdsJson = "[]",
            FirstSeenAt = lastSeen.AddDays(-100), LastSeenAt = lastSeen, LeftAt = leftAt,
        });
        await SaveAsync();
    }

    private async Task SeedEventAsync(string? guildId, string userId)
    {
        _db.DiscordMemberEvents.Add(new DiscordMemberEvent
        {
            GuildId = guildId, DiscordUserId = userId, SyncId = 1, Type = DiscordEventTypes.Joined, ObservedAt = Old,
        });
        await SaveAsync();
    }

    private async Task SeedRejectionAsync(string userId)
    {
        _db.DiscordLinkRejections.Add(new DiscordLinkRejection
        {
            DiscordUserId = userId, CitizenKey = "h:someone", ByApiUserId = 1, ByUsername = "user", CreatedAt = Old,
        });
        await SaveAsync();
    }

    private async Task SeedLinkAsync(string userId)
    {
        var entity = new TrackedEntity { CurrentHandle = $"linked{userId[^4..]}", CreatedAt = Old, UpdatedAt = Old };
        _db.TrackedEntities.Add(entity);
        await _db.SaveChangesAsync();
        _db.EntityLinks.Add(new EntityLink
        {
            TrackedEntityId = entity.Id, Provider = LinkProviders.Discord, Value = userId,
            AuthorApiUserId = 1, AuthorUsername = "user", CreatedAt = Old, UpdatedAt = Old,
        });
        await SaveAsync();
    }

    private async Task SaveAsync()
    {
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test src/Collector.Tests --filter "FullyQualifiedName~DiscordRetentionRepositoryTests"`

Expected: the build fails with `error CS0246: The type or namespace name 'DiscordRetentionRepository' could not be found`.

- [ ] **Step 3: Write the repository and register it**

Create `src/Collector/Data/Repositories/IDiscordRetentionRepository.cs`:

```csharp
namespace Collector.Data.Repositories;

/// <summary>
/// Deletes the Discord data the retention rules (spec § 13.3) no longer allow to keep. Each
/// call handles one batch in its own transaction, so the caller can release the Discord write
/// gate, and SQLite its write lock, between two batches.
/// </summary>
public interface IDiscordRetentionRepository
{
    /// <summary>
    /// Deletes up to <paramref name="batchSize"/> discord_syncs rows received before
    /// <paramref name="before"/>, oldest first. Returns how many were deleted.
    /// </summary>
    Task<int> PurgeSyncLogsAsync(DateTime before, int batchSize, CancellationToken ct = default);

    /// <summary>
    /// Purges up to <paramref name="batchSize"/> accounts no entity_links row points to and
    /// which either have member rows that all satisfy coalesce(LeftAt, LastSeenAt) &lt;
    /// <paramref name="before"/>, or have no member row and a LastSeenAt before it; with
    /// their discord_members, discord_member_events and discord_link_rejections rows.
    /// Returns how many accounts were purged.
    /// </summary>
    Task<int> PurgeDepartedAccountsAsync(DateTime before, int batchSize, CancellationToken ct = default);
}
```

Create `src/Collector/Data/Repositories/DiscordRetentionRepository.cs`:

```csharp
using Collector.Models;
using Microsoft.EntityFrameworkCore;

namespace Collector.Data.Repositories;

/// <summary>
/// Retention purges on the Discord tables, one bounded batch per call and per transaction
/// (see <see cref="IDiscordRetentionRepository"/>).
/// </summary>
public sealed class DiscordRetentionRepository(TrackerDbContext db) : IDiscordRetentionRepository
{
    public async Task<int> PurgeSyncLogsAsync(DateTime before, int batchSize, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var ids = await db.DiscordSyncs.AsNoTracking()
            .Where(s => s.ReceivedAt < before)
            .OrderBy(s => s.Id)
            .Select(s => s.Id)
            .Take(batchSize)
            .ToListAsync(ct);
        var deleted = ids.Count == 0 ? 0 : await db.DiscordSyncs.Where(s => ids.Contains(s.Id)).ExecuteDeleteAsync(ct);
        await tx.CommitAsync(ct);
        return deleted;
    }

    public async Task<int> PurgeDepartedAccountsAsync(DateTime before, int batchSize, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // Linked accounts are left out of the candidates, so they never fill a batch and stop
        // the caller's loop early.
        var candidates = await db.DiscordAccounts.AsNoTracking()
            .Where(a => !db.EntityLinks.Any(l => l.Provider == LinkProviders.Discord && l.Value == a.DiscordUserId))
            .Where(a => !db.DiscordMembers.Any(m => m.DiscordUserId == a.DiscordUserId
                && ((m.LeftAt != null && m.LeftAt >= before) || (m.LeftAt == null && m.LastSeenAt >= before))))
            .Where(a => a.LastSeenAt < before || db.DiscordMembers.Any(m => m.DiscordUserId == a.DiscordUserId))
            .OrderBy(a => a.Id)
            .Select(a => a.DiscordUserId)
            .Take(batchSize)
            .ToListAsync(ct);
        if (candidates.Count == 0)
        {
            await tx.CommitAsync(ct);
            return 0;
        }

        // entity_links is read again right before the deletes (spec § 13.3): a link made since
        // the candidates were chosen keeps its account.
        var linked = await db.EntityLinks.AsNoTracking()
            .Where(l => l.Provider == LinkProviders.Discord && candidates.Contains(l.Value))
            .Select(l => l.Value)
            .ToListAsync(ct);
        var ids = candidates.Except(linked, StringComparer.Ordinal).ToList();

        await db.DiscordMembers.Where(m => ids.Contains(m.DiscordUserId)).ExecuteDeleteAsync(ct);
        await db.DiscordMemberEvents.Where(e => ids.Contains(e.DiscordUserId)).ExecuteDeleteAsync(ct);
        await db.DiscordLinkRejections.Where(r => ids.Contains(r.DiscordUserId)).ExecuteDeleteAsync(ct);
        var purged = await db.DiscordAccounts.Where(a => ids.Contains(a.DiscordUserId)).ExecuteDeleteAsync(ct);
        await tx.CommitAsync(ct);
        return purged;
    }
}
```

In `src/Collector/Extensions/ServiceCollectionExtensions.cs`, method `AddCollectorDataServices`, replace:

```csharp
        services.AddScoped<IEntityLinkRepository, EntityLinkRepository>();
```

with:

```csharp
        services.AddScoped<IEntityLinkRepository, EntityLinkRepository>();
        services.AddScoped<IDiscordRetentionRepository, DiscordRetentionRepository>();
```

- [ ] **Step 4: Run the repository tests to verify they pass**

Run: `dotnet test src/Collector.Tests --filter "FullyQualifiedName~DiscordRetentionRepositoryTests"`

Expected: `Passed!  - Failed:     0, Passed:     8, Skipped:     0, Total:     8`

- [ ] **Step 5: Give the API tests a fake clock**

In `src/Collector.Api.Tests/Collector.Api.Tests.csproj`, replace (skip if an earlier task already added the reference):

```xml
    <PackageReference Include="FluentAssertions" />
    <PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" />
```

with:

```xml
    <PackageReference Include="FluentAssertions" />
    <PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" />
    <PackageReference Include="Microsoft.Extensions.TimeProvider.Testing" />
```

Run: `dotnet restore src/Collector.Api.Tests`

Expected: `Restored …Collector.Api.Tests.csproj`, and `git diff --stat src/Collector.Api.Tests/packages.lock.json` shows the lock file gained `Microsoft.Extensions.TimeProvider.Testing` (the version pinned in `Directory.Packages.props`, already used by `Collector.Tests`).

- [ ] **Step 6: Write the failing service tests**

Create `src/Collector.Api.Tests/Discord/DiscordRetentionServiceTests.cs`:

```csharp
using System.Globalization;
using Collector.Api.Options;
using Collector.Api.Services.Discord;
using Collector.Data;
using Collector.Extensions;
using Collector.Models;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Collector.Api.Tests.Discord;

/// <summary>
/// The retention pass runs after a startup delay and then once per interval, takes the
/// Discord write gate for every batch, and loops until a batch comes back short. It works on
/// its own tracker.db, so its purges never touch the shared test database; only the write
/// gate comes from the shared API host.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class DiscordRetentionServiceTests(ApiFactory factory) : IAsyncLifetime
{
    private static readonly DateTimeOffset Start = new(2026, 9, 30, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Older than both retention thresholds (365 and 730 days).</summary>
    private static readonly DateTime LongAgo = Start.UtcDateTime.AddDays(-800);

    private const string GuildId = "300000000000000001";
    private static long _next;

    private readonly string _dataDir =
        Path.Combine(Path.GetTempPath(), "sc-tracker-retention-tests", Guid.NewGuid().ToString("N"));
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dataDir);
        _provider = new ServiceCollection()
            .AddLogging()
            .AddCollectorDataServices(new ConfigurationBuilder().Build(), _dataDir)
            .BuildServiceProvider();
        await _provider.EnsureDatabaseAsync(_dataDir);
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, recursive: true); } catch (IOException) { /* SQLite handles may linger */ }
    }

    [Fact]
    public async Task APass_PurgesBatchAfterBatch_UntilABatchComesBackShort()
    {
        for (var i = 0; i < 5; i++) await SeedSyncAsync(LongAgo);
        await SeedSyncAsync(Start.UtcDateTime.AddDays(-1));
        for (var i = 0; i < 3; i++) await SeedAccountAsync(LongAgo);
        var linked = await SeedAccountAsync(LongAgo, linked: true);
        var present = await SeedAccountAsync(Start.UtcDateTime, activeInGuild: true);

        var result = await NewService(new FakeTimeProvider(Start), batchSize: 2)
            .RunOnceAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(30));

        result.Should().Be(new DiscordRetentionResult(SyncLogsDeleted: 5, AccountsPurged: 3));
        (await SyncCountAsync()).Should().Be(1);
        (await AccountIdsAsync()).Should().BeEquivalentTo(new[] { linked, present });
    }

    [Fact]
    public async Task EveryBatch_WaitsForTheDiscordWriteGate()
    {
        await SeedSyncAsync(LongAgo);
        var gate = factory.Services.GetRequiredService<DiscordWriteGate>();
        var service = NewService(new FakeTimeProvider(Start));
        Task<DiscordRetentionResult> run;

        using (await gate.EnterAsync(CancellationToken.None))
        {
            run = service.RunOnceAsync(CancellationToken.None);
            run.IsCompleted.Should().BeFalse("the gate is held by someone else");
            (await SyncCountAsync()).Should().Be(1);
        }

        (await run.WaitAsync(TimeSpan.FromSeconds(30))).SyncLogsDeleted.Should().Be(1);
        (await SyncCountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task TheFirstPass_FollowsTheStartupDelay_ThenOneComesEveryInterval()
    {
        var time = new FakeTimeProvider(Start);
        using var service = NewService(time);
        await SeedSyncAsync(LongAgo);

        await service.StartAsync(CancellationToken.None);
        time.Advance(service.StartupDelay - TimeSpan.FromSeconds(1));
        await Task.Delay(200);
        (await SyncCountAsync()).Should().Be(1, "the startup delay has not elapsed");

        time.Advance(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(async () => await SyncCountAsync() == 0);

        await SeedSyncAsync(LongAgo);
        time.Advance(service.Interval - TimeSpan.FromSeconds(1));
        await Task.Delay(200);
        (await SyncCountAsync()).Should().Be(1, "an interval has not passed since the first pass");

        time.Advance(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(async () => await SyncCountAsync() == 0);
        await service.StopAsync(CancellationToken.None);
    }

    private DiscordRetentionService NewService(TimeProvider time, int batchSize = DiscordRetentionService.DefaultBatchSize)
        => new(_provider.GetRequiredService<IServiceScopeFactory>(),
            factory.Services.GetRequiredService<DiscordWriteGate>(),
            Microsoft.Extensions.Options.Options.Create(new DiscordOptions()),
            time,
            NullLogger<DiscordRetentionService>.Instance)
        {
            BatchSize = batchSize,
        };

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 100 && !await condition(); i++) await Task.Delay(50);
        (await condition()).Should().BeTrue();
    }

    private static string NextId()
        => (400_000_000_000_000_000L + Interlocked.Increment(ref _next)).ToString(CultureInfo.InvariantCulture);

    private async Task WithDbAsync(Func<TrackerDbContext, Task> action)
    {
        await using var scope = _provider.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<TrackerDbContext>());
    }

    private Task SeedSyncAsync(DateTime receivedAt) => WithDbAsync(async db =>
    {
        db.DiscordSyncs.Add(new DiscordSync
        {
            GuildId = GuildId, SubmittedByApiUserId = 1, SubmittedByUsername = "sender",
            ReceivedAt = receivedAt, CollectedAt = receivedAt, DeclaredCollectedAt = receivedAt,
            Method = DiscordSyncMethods.MemberSearch, PluginVersion = "1.0.0",
        });
        await db.SaveChangesAsync();
    });

    private async Task<string> SeedAccountAsync(DateTime lastSeen, bool linked = false, bool activeInGuild = false)
    {
        var userId = NextId();
        await WithDbAsync(async db =>
        {
            db.DiscordAccounts.Add(new DiscordAccount
            {
                DiscordUserId = userId, Username = $"u{userId[^6..]}", FirstSeenAt = lastSeen, LastSeenAt = lastSeen,
            });
            if (activeInGuild)
                db.DiscordMembers.Add(new DiscordMember
                {
                    GuildId = GuildId, DiscordUserId = userId, RoleIdsJson = "[]", FirstSeenAt = lastSeen, LastSeenAt = lastSeen,
                });
            if (linked)
            {
                var entity = new TrackedEntity { CurrentHandle = $"linked{userId[^6..]}", CreatedAt = lastSeen, UpdatedAt = lastSeen };
                db.TrackedEntities.Add(entity);
                await db.SaveChangesAsync();
                db.EntityLinks.Add(new EntityLink
                {
                    TrackedEntityId = entity.Id, Provider = LinkProviders.Discord, Value = userId,
                    AuthorApiUserId = 1, AuthorUsername = "user", CreatedAt = lastSeen, UpdatedAt = lastSeen,
                });
            }
            await db.SaveChangesAsync();
        });
        return userId;
    }

    private async Task<int> SyncCountAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<TrackerDbContext>().DiscordSyncs.CountAsync();
    }

    private async Task<List<string>> AccountIdsAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<TrackerDbContext>()
            .DiscordAccounts.Select(a => a.DiscordUserId).ToListAsync();
    }
}
```

- [ ] **Step 7: Run them to verify they fail**

Run: `dotnet test src/Collector.Api.Tests --filter "FullyQualifiedName~DiscordRetentionServiceTests"`

Expected: the build fails with `error CS0246: The type or namespace name 'DiscordRetentionService' could not be found` (also `DiscordRetentionResult`).

- [ ] **Step 8: Write the service and register it**

Create `src/Collector.Api/Services/Discord/DiscordRetentionService.cs`:

```csharp
using Collector.Api.Options;
using Collector.Data.Repositories;
using Microsoft.Extensions.Options;

namespace Collector.Api.Services.Discord;

/// <summary>What one retention pass deleted.</summary>
public sealed record DiscordRetentionResult(int SyncLogsDeleted, int AccountsPurged);

/// <summary>
/// Applies the Discord retention rules once a day (spec § 13.3): sync log rows older than
/// <c>Discord:Retention:SyncLogDays</c>, and unlinked accounts gone for longer than
/// <c>Discord:Retention:DepartedAccountDays</c>. It works in batches of <see cref="BatchSize"/>
/// and takes the Discord write gate again for each one, so an ingestion never waits for a
/// whole pass. The first pass comes <see cref="StartupDelay"/> after start; the next ones
/// every <see cref="Interval"/>, anchored to that first pass.
/// </summary>
public sealed class DiscordRetentionService(
    IServiceScopeFactory scopes,
    DiscordWriteGate gate,
    IOptions<DiscordOptions> options,
    TimeProvider time,
    ILogger<DiscordRetentionService> logger) : BackgroundService
{
    public const int DefaultBatchSize = 500;

    /// <summary>Wait before the first pass, so startup and the first ingestions go first.</summary>
    public TimeSpan StartupDelay { get; init; } = TimeSpan.FromMinutes(10);

    public TimeSpan Interval { get; init; } = TimeSpan.FromDays(1);

    /// <summary>Rows (sync logs) or accounts per batch, and per transaction.</summary>
    public int BatchSize { get; init; } = DefaultBatchSize;

    /// <summary>One pass: sync logs first, then departed accounts, each until a batch comes back short.</summary>
    public async Task<DiscordRetentionResult> RunOnceAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var retention = options.Value.Retention;
        // At least a day: a zero or negative setting must never purge what was just received.
        var syncCutoff = now.AddDays(-Math.Max(1, retention.SyncLogDays));
        var accountCutoff = now.AddDays(-Math.Max(1, retention.DepartedAccountDays));

        var syncLogs = await PurgeInBatchesAsync(
            (repo, c) => repo.PurgeSyncLogsAsync(syncCutoff, BatchSize, c), ct);
        var accounts = await PurgeInBatchesAsync(
            (repo, c) => repo.PurgeDepartedAccountsAsync(accountCutoff, BatchSize, c), ct);
        return new DiscordRetentionResult(syncLogs, accounts);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var next = time.GetUtcNow() + StartupDelay;
        try
        {
            while (true)
            {
                var wait = next - time.GetUtcNow();
                if (wait > TimeSpan.Zero) await Task.Delay(wait, time, stoppingToken);
                await RunSafelyAsync(stoppingToken);

                next += Interval;
                var now = time.GetUtcNow();
                if (next <= now) next = now + Interval;
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task RunSafelyAsync(CancellationToken ct)
    {
        try
        {
            var result = await RunOnceAsync(ct);
            if (result.SyncLogsDeleted > 0 || result.AccountsPurged > 0)
                logger.LogInformation(
                    "Discord retention: deleted {SyncLogs} sync log rows, purged {Accounts} departed accounts",
                    result.SyncLogsDeleted, result.AccountsPurged);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Discord retention pass failed; next attempt in {Interval}", Interval);
        }
    }

    /// <summary>
    /// Runs batches, each in a fresh scope and under the write gate, until one deletes fewer
    /// than <see cref="BatchSize"/> rows. Every batch deletes what it counts, so the loop ends.
    /// </summary>
    private async Task<int> PurgeInBatchesAsync(
        Func<IDiscordRetentionRepository, CancellationToken, Task<int>> purgeBatch, CancellationToken ct)
    {
        var total = 0;
        int deleted;
        do
        {
            using (await gate.EnterAsync(ct))
            {
                await using var scope = scopes.CreateAsyncScope();
                deleted = await purgeBatch(scope.ServiceProvider.GetRequiredService<IDiscordRetentionRepository>(), ct);
            }
            total += deleted;
        }
        while (deleted >= BatchSize);
        return total;
    }
}
```

In `src/Collector.Api/Extensions/ServiceCollectionExtensions.cs`, method `AddApiServices`, replace:

```csharp
        services.AddHostedService<AudioOrphanSweeper>();
```

with:

```csharp
        services.AddHostedService<AudioOrphanSweeper>();

        // Discord retention (spec § 13.3): daily purge of old sync logs and departed accounts.
        // DiscordOptions is bound once, by lot A, a few lines below.
        if (!services.Any(d => d.ServiceType == typeof(TimeProvider)))
            services.AddSingleton(TimeProvider.System);
        services.AddHostedService<Collector.Api.Services.Discord.DiscordRetentionService>();
```

- [ ] **Step 9: Run the tests to verify they pass**

Run: `dotnet test src/Collector.Api.Tests --filter "FullyQualifiedName~DiscordRetentionServiceTests"`

Expected: `Passed!  - Failed:     0, Passed:     3, Skipped:     0, Total:     3`

Run: `dotnet test src/Collector.Tests --filter "FullyQualifiedName~DiscordRetentionRepositoryTests"`

Expected: `Passed!  - Failed:     0, Passed:     8, Skipped:     0, Total:     8`

- [ ] **Step 10: Run the whole API suite (the hosted service now starts with every test host)**

Run: `dotnet test src/Collector.Api.Tests`

Expected: `Passed!` with `Failed:     0`.

- [ ] **Step 11: Commit**

```bash
git add src/Collector/Data/Repositories/IDiscordRetentionRepository.cs \
        src/Collector/Data/Repositories/DiscordRetentionRepository.cs \
        src/Collector/Extensions/ServiceCollectionExtensions.cs \
        src/Collector.Api/Services/Discord/DiscordRetentionService.cs \
        src/Collector.Api/Extensions/ServiceCollectionExtensions.cs \
        src/Collector.Api.Tests/Collector.Api.Tests.csproj \
        src/Collector.Api.Tests/packages.lock.json \
        src/Collector.Tests/Discord/DiscordRetentionRepositoryTests.cs \
        src/Collector.Api.Tests/Discord/DiscordRetentionServiceTests.cs
git commit -F - <<'EOF'
feat(api): purge old discord sync logs and departed accounts daily

The data notice promises bounded retention: sync logs are kept a year and
the accounts of people gone from every tracked server two years, unless
someone linked them to an RSI citizen. A daily background pass deletes
them in batches of 500, each in its own transaction and under the Discord
write gate taken again per batch, so ingestions and link validations only
ever wait for one short batch. entity_links is read again right before
each delete, so a link made during the pass keeps its account.

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
EOF
```
