# Discord rosters — shared contracts for lots A, B and C

This file fixes every name, signature, route and JSON shape that crosses a task or lot
boundary. The three plans (`2026-09-30-discord-roster-lot-a-server.md`,
`2026-09-30-discord-roster-lot-b-plugin.md`, `2026-09-30-discord-roster-lot-c-read-ui.md`)
implement against it. When a plan and this file disagree, this file wins; change it first,
then the plans.

Spec: `docs/superpowers/specs/2026-09-30-discord-vencord-roster-design.md`.

## 0. Conventions

- **.NET**: SDK 10 (global.json), EF Core 8.0.31, xUnit 2.9 + FluentAssertions 7.
  - File-scoped namespaces.
  - `/// <summary>` on each class and on non-obvious members, explaining what and why.
  - Code, identifiers and comments are in English. User-facing web and plugin text is in
    French.
- **Tests**:
  - `src/Collector.Tests/Discord/*.cs` (namespace `Collector.Tests.Discord`).
  - `src/Collector.Api.Tests/Discord/*.cs` (namespace `Collector.Api.Tests.Discord`,
    `[Collection(ApiCollection.Name)]`, one shared `ApiFactory`, unique guild IDs and usernames
    per test).
- **Commands**:
  - `dotnet test src/Collector.Tests --filter "FullyQualifiedName~<Class>"`
  - `dotnet test src/Collector.Api.Tests --filter "FullyQualifiedName~<Class>"`
  - Web, **run from `src/Collector.Web`** (the repo root makes corepack pick another pnpm and
    fail with `ERR_PNPM_BAD_PM_VERSION`):
    - one file: `pnpm exec vitest run <file>` (`pnpm test -- <file>` would run the whole suite) ;
    - whole suite: `pnpm test` ;
    - types: `pnpm typecheck`.
  - Plugin, **run from `vencord/`**: `pnpm exec vitest run <file>`, `pnpm test`,
    `pnpm typecheck`.
- **EF migration** (lot A only): `dotnet tool restore`, then
  `dotnet ef migrations add AddDiscordRosters --project src/Collector --context TrackerDbContext`
  for tracker.db, and
  `dotnet ef migrations add AddApiKeyScope --project src/Collector.Api --context ApiDbContext`
  for api.db.
- **Commits**: Conventional Commits.
  - Scopes: `collector`, `api`, `web`, `deploy`, `vencord`, `docs`.
  - The subject is a lowercase, present-tense statement of the behaviour, with no trailing
    period.
  - The body explains why.
  - Last line: `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
  - Work happens on branch `lot-14-discord-roster`. Never push.
- **Snowflakes** are strings everywhere (C#, SQLite `TEXT`, JSON, TS).
  - `Collector.Discord.DiscordSnowflake.IsValid(string? s)`: `^[0-9]{17,20}$`.
  - Web zod: `snowflakeSchema`.
- **ISO dates in event values**: `Collector.Discord.DiscordFormats.Iso(DateTime utc)` returns
  `yyyy-MM-dd'T'HH:mm:ss'Z'` (InvariantCulture).

## 1. tracker.db entities (lot A, task "schema") — namespace `Collector.Models`

One file per entity in `src/Collector/Models/`, one `IEntityTypeConfiguration<T>` per entity
in `src/Collector/Data/Configurations/`, DbSets added to `TrackerDbContext`. All `DateTime`
values are UTC (global `UtcDateTimeConverter`). There are no foreign keys, following the
tracker.db convention. MaxLength is set on every string property.

```csharp
public class DiscordGuild            // table discord_guilds; unique GuildId; index OrgSid
{
    public long Id { get; set; }
    public string GuildId { get; set; } = null!;          // 20
    public string Name { get; set; } = null!;             // 100
    public string? IconHash { get; set; }                 // 34
    public string? OrgSid { get; set; }                   // 50, upper-case
    public long? OrgMappedByApiUserId { get; set; }
    public string? OrgMappedByUsername { get; set; }      // 100
    public DateTime? OrgMappedAt { get; set; }
    public int? MemberCount { get; set; }
    public DateTime FirstSyncAt { get; set; }             // CollectedAt of the baseline
    public DateTime LastSyncAt { get; set; }              // ReceivedAt of last accepted sync
    public DateTime LastCollectedAt { get; set; }         // CollectedAt of last accepted sync
    public DateTime? LastCompleteSyncAt { get; set; }     // CollectedAt of last complete sync
    public bool AllowMassDepartureOnce { get; set; }     // unused legacy SQLite column; no manual allowance
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class DiscordRole             // table discord_roles; unique (GuildId, RoleId)
{
    public long Id { get; set; }
    public string GuildId { get; set; } = null!;          // 20
    public string RoleId { get; set; } = null!;           // 20
    public string Name { get; set; } = null!;             // 100
    public int Position { get; set; }
    public string? Color { get; set; }                    // 7, "#rrggbb"
    public bool Hoist { get; set; }
    public bool Managed { get; set; }
    public bool IsRank { get; set; }                      // set on insert: Hoist && !Managed; then site-only
    public int? RankOrder { get; set; }                   // non-null for every rank role
    public string? RsiRankLabel { get; set; }             // 100
    public DateTime FirstSeenAt { get; set; }
    public DateTime LastSeenAt { get; set; }
    public DateTime? DeletedAt { get; set; }
}

public class DiscordAccount          // table discord_accounts; unique DiscordUserId
{
    public long Id { get; set; }
    public string DiscordUserId { get; set; } = null!;    // 20
    public string Username { get; set; } = null!;         // 32
    public string? GlobalName { get; set; }               // 32
    public bool IsBot { get; set; }
    public DateTime FirstSeenAt { get; set; }
    public DateTime LastSeenAt { get; set; }
}

public class DiscordMember           // table discord_members; unique (GuildId, DiscordUserId); index DiscordUserId; index (GuildId, LeftAt)
{
    public long Id { get; set; }
    public string GuildId { get; set; } = null!;          // 20
    public string DiscordUserId { get; set; } = null!;    // 20
    public string? Nick { get; set; }                     // 32
    public string RoleIdsJson { get; set; } = "[]";       // sorted JSON array of role id strings
    public DateTime? JoinedAt { get; set; }               // truncated to the second
    public DateTime FirstSeenAt { get; set; }
    public DateTime LastSeenAt { get; set; }
    public DateTime? LeftAt { get; set; }
}

public class DiscordMemberEvent      // table discord_member_events; index (GuildId, Id); index (DiscordUserId, Id); index SyncId
{
    public long Id { get; set; }
    public string? GuildId { get; set; }                  // 20; null for account-level events
    public string DiscordUserId { get; set; } = null!;    // 20
    public long SyncId { get; set; }
    public string Type { get; set; } = null!;             // 30, DiscordEventTypes
    public string? OldValue { get; set; }                 // 4000
    public string? NewValue { get; set; }                 // 4000
    public DateTime? OccurredAt { get; set; }
    public DateTime? NotBefore { get; set; }
    public DateTime ObservedAt { get; set; }
}

public class DiscordSync             // table discord_syncs; index (GuildId, Id)
{
    public long Id { get; set; }
    public string GuildId { get; set; } = null!;          // 20
    public long SubmittedByApiUserId { get; set; }
    public string SubmittedByUsername { get; set; } = null!; // 100
    public DateTime ReceivedAt { get; set; }
    public DateTime CollectedAt { get; set; }             // ReceivedAt − collectionDurationMs
    public DateTime DeclaredCollectedAt { get; set; }     // plugin clock, informative
    public string Method { get; set; } = null!;           // 20, DiscordSyncMethods
    public bool DeclaredComplete { get; set; }
    public bool IsComplete { get; set; }                  // server-recomputed coverage, independent of departure count
    public bool IsBaseline { get; set; }
    public bool MassDepartureDetected { get; set; }     // informational; mapped to legacy DepartureGuardTripped column
    public int? ExpectedCount { get; set; }
    public int CollectedCount { get; set; }
    public int OptedOutCount { get; set; }
    public int UnknownRoleRefCount { get; set; }
    public int EventCount { get; set; }
    public string PluginVersion { get; set; } = null!;    // 20
}

public class DiscordOptOut           // table discord_optouts; unique DiscordUserId
{
    public long Id { get; set; }
    public string DiscordUserId { get; set; } = null!;    // 20
    public DateTime CreatedAt { get; set; }
    public long? ByApiUserId { get; set; }                // null when the actor is the static admin key
    public string ByUsername { get; set; } = null!;       // 100
    public string? Reason { get; set; }                   // 500
}

public class DiscordGuildOptOut      // table discord_guild_optouts; unique GuildId
{
    public long Id { get; set; }
    public string GuildId { get; set; } = null!;          // 20
    public DateTime CreatedAt { get; set; }
    public long? ByApiUserId { get; set; }
    public string ByUsername { get; set; } = null!;       // 100
    public string? Reason { get; set; }                   // 500
}

public class DiscordLinkRejection    // table discord_link_rejections; unique (DiscordUserId, CitizenKey)
{
    public long Id { get; set; }
    public string DiscordUserId { get; set; } = null!;    // 20
    public string CitizenKey { get; set; } = null!;       // 100: CitizenId as decimal string, or "h:" + lower-case handle
    public long ByApiUserId { get; set; }
    public string ByUsername { get; set; } = null!;       // 100
    public DateTime CreatedAt { get; set; }
}

public static class DiscordEventTypes
{
    public const string Joined = "joined", Left = "left", Rejoined = "rejoined",
        RolesChanged = "roles_changed", NickChanged = "nick_changed",
        UsernameChanged = "username_changed", GlobalNameChanged = "global_name_changed";
}

public static class DiscordSyncMethods
{
    public const string MemberSearch = "member-search", RoleMembers = "role-members", Cache = "cache";
    public static bool IsValid(string? m) => m is MemberSearch or RoleMembers or Cache;
}
```

DbSet names on `TrackerDbContext`:

| DbSet | Entity |
|---|---|
| `DiscordGuilds` | `DiscordGuild` |
| `DiscordRoles` | `DiscordRole` |
| `DiscordAccounts` | `DiscordAccount` |
| `DiscordMembers` | `DiscordMember` |
| `DiscordMemberEvents` | `DiscordMemberEvent` |
| `DiscordSyncs` | `DiscordSync` |
| `DiscordOptOuts` | `DiscordOptOut` |
| `DiscordGuildOptOuts` | `DiscordGuildOptOut` |
| `DiscordLinkRejections` | `DiscordLinkRejection` |

Migration `AddDiscordRosters` (one migration) also adds:

- `IX_entity_links_Provider_Value` on `entity_links (Provider, Value)`, configured with
  `HasIndex` in `EntityLinkConfiguration` ;
- in raw SQL, following the `IndexCleanup` pattern (`CREATE INDEX IF NOT EXISTS`, and
  `DROP INDEX IF EXISTS` in `Down`) :
  - `IX_users_UserHandle_NoCase ON users (UserHandle COLLATE NOCASE)` ;
  - `IX_user_handle_history_UserHandle_NoCase ON user_handle_history (UserHandle COLLATE NOCASE)`.

## 2. Pure ingest model — namespace `Collector.Discord`, folder `src/Collector/Discord/`

These types have no dependency on EF or ASP.NET.

- `DiscordSnowflake`, `DiscordFormats`: `DiscordSnowflake.cs`, `DiscordFormats.cs`.
- Records: `RosterRecords.cs`.
- Diff engine: `DiscordRosterDiff.cs`.
- Rank resolver and tokenizer: lot C (§ 7).

```csharp
public sealed record NormalizedRole(string RoleId, string Name, int Position, string? Color, bool Hoist, bool Managed);

public sealed record NormalizedMember(
    string UserId, string Username, string? GlobalName, string? Nick,
    IReadOnlyList<string> RoleIds,   // sorted ordinal, distinct, only ids present in NormalizedSync.Roles
    DateTime? JoinedAt,              // UTC, truncated to the second
    bool IsBot);

public sealed record NormalizedSync(
    string GuildId, string GuildName, string? IconHash, int? MemberCount,
    string Method, bool DeclaredComplete,
    bool IsComplete,                 // DeclaredComplete && Method == member-search && raw Members.Count == ExpectedCount
    int? ExpectedCount, int CollectedCount /* raw members count */,
    string PluginVersion, DateTime DeclaredCollectedAt /* UTC */, TimeSpan CollectionDuration,
    IReadOnlyList<NormalizedRole> Roles, IReadOnlyList<NormalizedMember> Members,
    int UnknownRoleRefCount);

public sealed record GuildSnapshot(DateTime FirstSyncAt, DateTime? LastCompleteSyncAt, bool BaselinePending = false);
public sealed record RoleSnapshot(string RoleId, string Name, bool IsDeleted);
public sealed record MemberSnapshot(
    string UserId, string? Nick, IReadOnlyList<string> RoleIds, DateTime? JoinedAt,
    DateTime LastSeenAt, DateTime? LeftAt, long? LastLeftEventId);
public sealed record AccountSnapshot(string UserId, string Username, string? GlobalName, bool IsBot = false, DateTime? LastSeenAt = null);
public sealed record RosterSnapshot(
    GuildSnapshot? Guild,                                   // null => this sync is the baseline
    IReadOnlyDictionary<string, RoleSnapshot> Roles,        // every stored role of the guild, deleted ones included
    IReadOnlyDictionary<string, MemberSnapshot> Members,    // every stored member row of the guild (active and left)
    IReadOnlyDictionary<string, AccountSnapshot> Accounts); // stored accounts among the payload's user ids

public sealed record PlannedEvent(
    string? GuildId, string UserId, string Type, string? OldValue, string? NewValue,
    DateTime? OccurredAt, DateTime? NotBefore, DateTime ObservedAt);
public sealed record MemberWrite(string UserId, string? Nick, IReadOnlyList<string> RoleIds, DateTime? JoinedAt, DateTime? LeftAt);
public sealed record AccountWrite(string UserId, string Username, string? GlobalName, bool IsBot);

public sealed record RosterPlan(
    bool IsBaseline,
    bool IsComplete,                        // server-validated coverage; mass departures keep it complete
    bool MassDepartureDetected,             // informational: >= 10 departures and > 25% of active members
    int OptedOutCount,
    IReadOnlyList<NormalizedRole> RolesToInsert,
    IReadOnlyList<NormalizedRole> RolesToUpdate,        // includes restored roles (DeletedAt cleared)
    IReadOnlyList<string> RoleIdsToMarkDeleted,
    IReadOnlyList<AccountWrite> AccountsToInsert,
    IReadOnlyList<AccountWrite> AccountsToUpdate,       // name/global-name/bot changes
    IReadOnlyList<MemberWrite> MembersToInsert,
    IReadOnlyList<MemberWrite> MembersToUpdate,         // full target state (departures set LeftAt = collectedAt)
    IReadOnlyList<PlannedEvent> Events,
    IReadOnlyList<long> EventIdsToDelete,               // "false departure" corrections
    IReadOnlyList<string> PresentUserIds);              // non-opted-out payload members (LastSeenAt touch)

public static class DiscordRosterDiff
{
    public const double MassDepartureRatio = 0.25;
    public const int MassDepartureMinimum = 10;
    public static RosterPlan Compute(string guildId, NormalizedSync sync, DateTime collectedAt,
        RosterSnapshot snapshot, IReadOnlySet<string> optedOutUserIds);
}
```

The rules are in spec § 9.3, implemented exactly.

- Event values for `roles_changed`: JSON array `[{"id":"…","name":"…"}]`, compact and ordered
  by role id. Old names come from `RoleSnapshot.Name`, new names from the payload (or from the
  snapshot for a role missing from the payload).
- Other event values follow spec § 8, with dates through `DiscordFormats.Iso`.
- `DiscordStoreBusyException : Exception` (same folder) is thrown by the repository when SQLite
  stays busy after one retry.

## 3. Repositories (lot A) — namespace `Collector.Data.Repositories`

Registered as scoped in `AddCollectorDataServices`.

```csharp
public sealed record DiscordSyncWrite(
    NormalizedSync Sync, RosterPlan Plan, DateTime CollectedAt, DateTime ReceivedAt,
    long SubmittedByApiUserId, string SubmittedByUsername);
public sealed record DiscordSyncResult(long SyncId, string? OrgSid);

public interface IDiscordRosterRepository
{
    Task<bool> IsGuildExcludedAsync(string guildId, CancellationToken ct = default);
    Task<DateTime?> GetLastSyncReceivedAtAsync(string guildId, CancellationToken ct = default);
    Task<RosterSnapshot> LoadSnapshotAsync(string guildId, IReadOnlyCollection<string> payloadUserIds, CancellationToken ct = default);
    Task<IReadOnlySet<string>> GetOptedOutAsync(IReadOnlyCollection<string> userIds, CancellationToken ct = default);
    /// Persists a pending journal and baseline guild metadata first. Writes each account/member
    /// effect together with its events in transactions of at most MaxRowsPerTransaction rows,
    /// then monotone LastSeenAt batches. Publishes roles, guild metadata and the completed
    /// journal only after all effects commit. Retries a transaction once after 2 s on
    /// SQLITE_BUSY, then throws DiscordStoreBusyException.
    Task<DiscordSyncResult> ApplyAsync(DiscordSyncWrite write, CancellationToken ct = default);
}
// DiscordRosterRepository: public const int MaxRowsPerTransaction = 5000;

public interface IDiscordErasureRepository
{
    Task EraseAccountAsync(string discordUserId, long? byApiUserId, string byUsername, CancellationToken ct = default); // always leaves an opt-out row
    Task<bool> EraseGuildAsync(string guildId, bool exclude, long? byApiUserId, string byUsername, CancellationToken ct = default); // false: unknown guild and exclude == false
    Task<IReadOnlyList<DiscordOptOut>> ListOptOutsAsync(CancellationToken ct = default);
    Task<bool> RemoveOptOutAsync(string discordUserId, CancellationToken ct = default);
    Task<IReadOnlyList<DiscordGuildOptOut>> ListGuildOptOutsAsync(CancellationToken ct = default);
    Task<bool> RemoveGuildOptOutAsync(string guildId, CancellationToken ct = default);
}
```

- `EraseAccountAsync` deletes the account's `discord_members`, `discord_member_events` and
  `discord_accounts` rows, its `discord_link_rejections`, and its `entity_links` (Provider
  `discord`, Value = id).
- `EraseGuildAsync` deletes the guild's `discord_roles`, `discord_members`,
  `discord_member_events`, `discord_syncs` and `discord_guilds` rows. In the same operation it
  deletes the **unlinked** accounts left with no member row, with their events (`GuildId` null)
  and rejections.

**Bounded writes and interrupted attempts (implementation corrections)**:

- Every transaction writes at most 5,000 physical rows, including inserts, updates, deletes
  and events. A member/account effect and its associated events commit together. The old
  plan's single final transaction for all changes would violate this budget on a large sync.
- A pending `DiscordSync` has `IsComplete = false` and `EventCount = -1`. Legacy negative
  markers, including `-2`, still resume without any allowance. Its author, received/collected dates,
  declared coverage and plugin metadata are durable before effects begin. No new column is needed.
- Sync lists, latest-sync summaries and event/timeline reads exclude pending journal entries
  (`EventCount >= 0` on the associated sync). Retention neither removes a pending sync nor
  purges an account recently touched by an unfinished attempt.
- The next accepted attempt closes each interrupted entry as **partial**, keeps its original
  author and metadata, and sets its actual committed event count. It never relabels an
  interrupted attempt as complete. Already committed effects/events remain; the new diff
  writes only missing effects. Remaining baseline rows stay silent via `BaselinePending`.
- `FirstSyncAt` remains the first attempt's collection date. The guild's latest completed
  metadata (including `LastCompleteSyncAt`) advances only after effects and freshness batches
  commit. Recovery can advance `LastSyncAt`/`LastCollectedAt` to the interrupted receipt's
  original dates. The stale check includes pending receipts, preventing older collections
  from overwriting their already durable effects.
- Every complete observation records all absent active members as departed. Mass departures
  only produce a signal; partial observations produce no departures. No allowance is consumed.
- Account identity spans guilds: a collection older than the stored account `LastSeenAt`
  cannot rewrite its username, global name or bot flag or create global rename events.
  Guild membership observations still apply according to the per-guild stale check.
- Account erasure writes its opt-out first, preserves its original actor on retry, then
  deletes history and related rows in bounded batches. Guild erasure writes any exclusion
  first and deletes the guild row last. Prospective orphan membership rows remain until
  their global history/rejections are removed; membership+account deletion then commits
  together, preserving a retry pointer if cleanup is interrupted.
- Guild erasure also sweeps globally orphaned, unlinked accounts: an interrupted ingestion
  may have inserted accounts before its first member batch. Each account stays as the durable
  cleanup pointer until its global events/rejections are deleted in bounded batches.

## 4. API — auth, errors, limits (lot A)

```csharp
namespace Collector.Api.Auth;
public static class DiscordIngestAuth
{
    public const string SchemeName = "DiscordIngestKey";
    public const string PolicyName = "DiscordIngest";
    public const string ScopeClaimType = "scope";
    public const string IngestScope = "discord:ingest";
    public const string PathPrefix = "/api/ingest/discord";
}
// DiscordIngestKeyAuthHandler : AuthenticationHandler<ApiKeySchemeOptions>, reads x-api-key, accepts ONLY keys whose Scope == IngestScope.

namespace Collector.Api.Models;
public static class ApiKeyScopes { public const string DiscordIngest = "discord:ingest"; public static bool IsValid(string? s) => s is null or DiscordIngest; }
// ApiKey gains: public string? Scope { get; set; }  (api.db column, MaxLength 30, migration AddApiKeyScope)

namespace Collector.Api.Services;
public sealed record ApiKeyValidation(ApiUser User, string? Scope);
// ApiKeyService.ValidateAsync(string rawKey, CancellationToken ct = default) now returns Task<ApiKeyValidation?>
// CreateApiKeyRequest(string Name, DateTime? ExpiresAt, string? Scope = null)
// ApiKeyDto gains: public string? Scope { get; set; }
```

- `Smart` selector: a path starting with `DiscordIngestAuth.PathPrefix` (OrdinalIgnoreCase)
  goes to `DiscordIngestKey`, otherwise to the current rule (JWT if `Authorization`, else
  `ApiKey`).
- `ApiKeyAuthHandler` returns `Fail("Scoped key")` for a key whose Scope is not null.
- Policy `DiscordIngest` uses scheme `DiscordIngestKey`, requires an authenticated user and
  `RequireClaim(ScopeClaimType, IngestScope)`.

**Errors.**

- `DomainException` gains an optional `string? Code` (constructor parameter
  `string? code = null`). `ConflictException(string message, string? code = null)` and
  `ValidationException(string message, string? code = null)` pass it through.
- New `ServiceUnavailableException(string message, int retryAfterSeconds, string? code = "busy")`
  (503).
- `ExceptionHandlingMiddleware` behaviour:
  - writes `problem.Extensions["code"]` when `Code` is not null ;
  - sets the `Retry-After` header for `ServiceUnavailableException` ;
  - maps `Microsoft.AspNetCore.Http.BadHttpRequestException` to its `StatusCode` (413 for an
    oversized body), with title `"Payload Too Large"` for 413 and the default reason phrase
    otherwise, logged at Information level with no stack trace.
- Wire shape: `{"type","title","status","instance","detail","correlationId","code"}`.

Error codes:

| Code | Status | When |
|---|---|---|
| `invalid_sync` | 400 | body validation |
| `empty_complete_sync` | 400 | `complete: true` with no members |
| `stale_sync` | 409 | a newer sync arrived during the collection |
| `guild_excluded` | 409 | the guild is in `discord_guild_optouts` |
| `busy` | 503 | gate timeout or SQLite still busy |

**Rate limiting.**

- `RateLimitSettings.DiscordIngest { int PermitLimit = 20; int WindowSeconds = 600; }` (section
  `Api:RateLimit:DiscordIngest`).
- `RateLimitingExtensions.DiscordIngestPolicy = "discord-ingest"`, partitioned
  `discord-ingest:user:{NameIdentifier}`, or `discord-ingest:ip:{ip}` when anonymous.
- `options.OnRejected` sets `Retry-After` from `MetadataName.RetryAfter` when available.
- `ApiFactory` adds `COLLECTOR_API_Api__RateLimit__DiscordIngest__PermitLimit=100000`.

**Options** (`Collector.Api.Options.DiscordOptions`, section `Discord`):

```csharp
public sealed class DiscordOptions
{
    public const string Section = "Discord";
    public IngestOptions Ingest { get; set; } = new();
    public RetentionOptions Retention { get; set; } = new();
    public sealed class IngestOptions { public string? PublicUrl { get; set; } public string? CertificateSha256 { get; set; } }
    public sealed class RetentionOptions { public int SyncLogDays { get; set; } = 365; public int DepartedAccountDays { get; set; } = 730; }
}
```

## 5. API — ingest (lot A)

**Wire DTOs**, in `src/Collector.Api/Dtos/Discord/DiscordIngestDtos.cs`, namespace
`Collector.Api.Dtos.Discord`, camelCase JSON:

```csharp
public sealed record DiscordSyncRequest(
    string PluginVersion, DateTimeOffset CollectedAt, long CollectionDurationMs,
    DiscordSyncGuild Guild, DiscordSyncCoverage Coverage,
    IReadOnlyList<DiscordSyncRole> Roles, IReadOnlyList<DiscordSyncMember> Members);
public sealed record DiscordSyncGuild(string Id, string Name, string? Icon, int? MemberCount);
public sealed record DiscordSyncCoverage(string Method, bool Complete, int? ExpectedCount, int CollectedCount);
public sealed record DiscordSyncRole(string Id, string Name, int Position, string? Color, bool Hoist, bool Managed);
public sealed record DiscordSyncMember(
    string UserId, string Username, string? GlobalName, string? Nick,
    IReadOnlyList<string> RoleIds, DateTimeOffset? JoinedAt, bool Bot);

public sealed class DiscordSyncEventCountsDto
{ public int Joined { get; set; } public int Left { get; set; } public int Rejoined { get; set; }
  public int RolesChanged { get; set; } public int NickChanged { get; set; } public int NameChanged { get; set; } }

public sealed class DiscordSyncResponseDto
{
    public long SyncId { get; set; }
    public bool IsBaseline { get; set; }
    public bool IsComplete { get; set; }
    public bool MassDepartureDetected { get; set; }
    public bool DepartureGuardTripped => false; // compatibility with installed older plugins
    public string? OrgSid { get; set; }
    public int MembersReceived { get; set; }
    public int MembersOptedOut { get; set; }
    public int UnknownRoleRefs { get; set; }
    public DiscordSyncEventCountsDto Events { get; set; } = new();
}
```

**Services and controller** (namespace `Collector.Api.Services.Discord`, folder
`src/Collector.Api/Services/Discord/`):

- `DiscordSyncValidator.Normalize(string routeGuildId, DiscordSyncRequest r) : NormalizedSync`
  (static). It throws `ValidationException(…, "invalid_sync" | "empty_complete_sync")` and
  applies spec § 7.3, bounds and normalisation included.
- `DiscordWriteGate` (singleton) has two methods:
  - `Task<IDisposable?> TryEnterAsync(TimeSpan timeout, CancellationToken ct)`, which returns
    null on timeout ;
  - `Task<IDisposable> EnterAsync(CancellationToken ct)`.
- `DiscordIngestGateFilter : IAsyncResourceFilter`, applied with
  `[TypeFilter(typeof(DiscordIngestGateFilter))]`:
  1. validates the route `guildId` (`invalid_sync`) ;
     request arrival is captured first, before exclusion reads, model binding and gate waiting ;
  2. refuses an excluded guild (`guild_excluded`) ;
  3. calls `TryEnterAsync(10 s)`, or throws `ServiceUnavailableException(…, 30)` ;
  4. stores the lease in `HttpContext.Items["DiscordWriteGate"]` and releases it in a
     `finally`.
- `DiscordIngestService.IngestAsync(string guildId, DiscordSyncRequest request, DateTime
  receivedAt, long submitterId, string submitterName, CancellationToken ct) :
  Task<DiscordSyncResponseDto>` (scoped). It runs with the gate held:
  1. normalise ;
  2. `CollectedAt = receivedAt − duration` ;
  3. stale check against `GetLastSyncReceivedAtAsync` (`stale_sync`) ;
  4. load the snapshot and opt-outs ;
  5. `DiscordRosterDiff.Compute` ;
  6. `ApplyAsync`, mapping `DiscordStoreBusyException` to `ServiceUnavailableException(…, 30)`.
- `DiscordIngestController` has
  `[ApiController, Route("api/ingest/discord"), Authorize(Policy = DiscordIngestAuth.PolicyName), EnableRateLimiting(RateLimitingExtensions.DiscordIngestPolicy)]`.
  - Action: `[HttpPost("guilds/{guildId}/syncs"), RequestSizeLimit(MaxBodyBytes), TypeFilter(typeof(DiscordIngestGateFilter))]`
    `Task<ActionResult<DiscordSyncResponseDto>> PostSync(string guildId, [FromBody] DiscordSyncRequest request, CancellationToken ct)`.
  - `public const long MaxBodyBytes = 25L * 1024 * 1024;`

**Audit**: `DiscordAudit.LogAsync(ActivityLogService logs, CurrentUserAccessor user, ILogger
logger, string action, string entityType, string entityId, CancellationToken ct)` (static). It
passes `userId = UserId > 0 ? UserId : null` and never throws: a failure is logged as a warning.

**Admin controller**: `DiscordAdminController`, `[ApiController, Route("api/discord"),
Authorize(Policy = "AdminOnly")]`. Every action takes `DiscordWriteGate.TryEnterAsync(10 s)`
(or 503) and writes an audit entry.

| Route | Response | Audit action |
|---|---|---|
| `DELETE accounts/{discordUserId}` | 204 | `discord_erase_account` |
| `DELETE guilds/{guildId}?exclude=true\|false` | 204, or 404 | `discord_erase_guild` |
| `GET optouts` | `IReadOnlyList<DiscordOptOutDto>` | — |
| `DELETE optouts/{discordUserId}` | 204, or 404 | `discord_remove_optout` |
| `GET guild-optouts` | `IReadOnlyList<DiscordGuildOptOutDto>` | — |
| `DELETE guild-optouts/{guildId}` | 204, or 404 | `discord_remove_guild_optout` |

The DTOs are:

- `DiscordOptOutDto { DiscordUserId, CreatedAt, ByUsername, Reason }` ;
- `DiscordGuildOptOutDto { GuildId, CreatedAt, ByUsername, Reason }`.

**Ingest config**: `GET api/discord/ingest-config`, an action added to the existing
`DiscordController`, returns `DiscordIngestConfigDto { string? PublicUrl, string?
CertificateSha256 }` from `DiscordOptions.Ingest`.

**Test kit**: `src/Collector.Api.Tests/Discord/DiscordTestKit.cs`.

```csharp
public static class DiscordTestKit
{
    // Creates a user, signs in, creates a discord:ingest key (180 days) and returns a client carrying only x-api-key.
    public static Task<(HttpClient Client, string RawKey, long UserId)> IngestClientAsync(ApiFactory f, string username);
    public static string NewSnowflake();   // unique 18-digit string per call (thread-safe counter)
    public static DiscordSyncRequest Sync(string guildId, IEnumerable<DiscordSyncMember> members,
        IEnumerable<DiscordSyncRole>? roles = null, bool complete = true, string method = "member-search",
        long durationMs = 1000, int? expectedCount = null);   // expectedCount defaults to members.Count()
    public static DiscordSyncMember Member(string userId, string username, IEnumerable<string>? roleIds = null,
        string? nick = null, string? globalName = null, DateTimeOffset? joinedAt = null, bool bot = false);
    public static DiscordSyncRole Role(string roleId, string name, int position, bool hoist = true, bool managed = false);
    public static Task<HttpResponseMessage> PostSyncAsync(HttpClient ingestClient, string guildId, DiscordSyncRequest body);
}
```

## 6. Web — settings key panel (lot A)

- `lib/api/types.ts` gains the following types :
  - `ApiKeyDto { id: number; name: string; keyPrefix: string; createdAt: string; lastUsedAt: string | null; expiresAt: string | null; isRevoked: boolean; scope: string | null }` ;
  - `CreatedApiKeyDto = ApiKeyDto & { rawKey: string }` ;
  - `DiscordIngestConfigDto { publicUrl: string | null; certificateSha256: string | null }`.
- `lib/api/endpoints.ts` gains `listApiKeys(ctx)` and `getDiscordIngestConfig(ctx)`.
- `lib/validation.ts` gains `apiKeyNameSchema` (trimmed, 1–100 characters) and
  `ingestKeyExpiryDaysSchema` (integer, 1–365).
- `src/app/(user)/settings/discord-key-actions.ts` (`"use server"`):
  - `createDiscordIngestKeyAction(name: unknown, expiresInDays: unknown): Promise<{ ok: boolean; data?: CreatedApiKeyDto; error?: string }>`
    POSTs `{ name, expiresAt, scope: "discord:ingest" }` to `/api/api-keys` ;
  - `revokeApiKeyAction(id: unknown): Promise<{ ok: boolean; error?: string }>`.
- `src/app/(user)/settings/DiscordIngestKeysPanel.tsx`, a client component, replaces the "v2"
  placeholder in `settings/page.tsx`.

## 7. Lot C — reads, links, UI

**Pure helpers** (`Collector.Discord`):

```csharp
public sealed record RankRole(string RoleId, string Name, string? Color);
public static class DiscordRankResolver
{
    // Among the member's non-deleted rank roles: RankOrder desc, then Position desc, then RoleId ordinal.
    public static RankRole? Resolve(IEnumerable<string> memberRoleIds, IReadOnlyDictionary<string, DiscordRole> guildRoles);
}
public static class HandleTokenizer
{
    // Spec § 10.1 normalisation: strip [..] (..) {..} «..» segments, split on [^A-Za-z0-9_-],
    // keep 3–60 char tokens, add the whole string without whitespace/emoji; distinct, OrdinalIgnoreCase.
    public static IReadOnlyList<string> Tokens(string? value);
    public static IReadOnlyList<string> Candidates(string? nick, string? globalName, string username);
}
```

**Read services** (`Collector.Api.Services.Discord`, all scoped):

| Service | Covers |
|---|---|
| `DiscordRosterQueryService` | guild list and detail, members page, events, syncs |
| `DiscordReconciliationService` | statuses, discrepancies, multi-membership |
| `DiscordSuggestionService` | suggestions, links, rejections |
| `DiscordProfileService` | user cross profile, org guilds |
| `DiscordGuildConfigService` | PUT org and PUT role, responsible-user rule |
| `DiscordRetentionService` | `BackgroundService`, daily |

**Retention writes (C7)**: each pass takes the Discord write gate per batch of 500
receipts or candidate accounts. Pending receipts (`EventCount < 0`) remain available
for ingestion recovery. Accounts need an old `LastSeenAt` as well as no recent or active
membership observation, and linked accounts are excluded and rechecked before deletion.
Events, rejections and memberships are deleted in separate transactions of at most
5,000 physical rows; the account is deleted last, so interruption leaves a durable
candidate for the next pass. Receipt deletion follows the same transaction row bound
and SQLite retry policy. The first pass is ten minutes after host startup, then daily.

**RSI timeline identity windows (C4)**: `UserHandleHistory.FirstSeen` and `LastSeen`
are observation dates, not exact dates of acquisition or transfer. A handle can have
several rows for one citizen, and its successive passes must remain separate.
Without observed reuse, the usual history is preserved up to the next handle's
first observation; the current handle has no artificial lower bound. A handle
observed for another citizen, or taken back after an intervening handle, uses closed
observation intervals, with the current user's `UpdatedAt` extending the latest
pass. Unobserved gaps are omitted. Overlapping owners make an interval ambiguous:
only its observed endpoints outside the other owners' intervals remain. An entity
without a citizen id cannot claim a contested handle. User-level `handle_changed`
events carry the citizen id in `EntityId` and are read by that permanent identity;
member events only carry a handle and use these windows. Physical events are
deduplicated before the combined timeline's final 100-entry limit.

**Controller**: `DiscordRostersController`, `[ApiController, Route("api"), Authorize]`.

JSON (camelCase), with DTO classes in
`src/Collector.Api/Dtos/Discord/DiscordRosterDtos.cs`:

```ts
type DiscordRankDto = { roleId: string; name: string; color: string | null };
type DiscordLastSyncDto = { receivedAt: string; isComplete: boolean; method: string; submittedBy: string; massDepartureDetected: boolean };
type DiscordGuildSummaryDto = {
  guildId: string; name: string; iconHash: string | null;
  orgSid: string | null; orgName: string | null; orgMappedBy: string | null;
  activeMembers: number;                       // non-bot, LeftAt null
  rankDistribution: { roleId: string; name: string; color: string | null; count: number }[];
  lastSync: DiscordLastSyncDto | null; lastCompleteSyncAt: string | null };
type DiscordRoleDto = { roleId: string; name: string; position: number; color: string | null; hoist: boolean; managed: boolean;
  isRank: boolean; rankOrder: number | null; rsiRankLabel: string | null; deleted: boolean; memberCount: number };
type DiscordGuildDetailDto = DiscordGuildSummaryDto & { roles: DiscordRoleDto[]; rsiRanks: string[]; canEdit: boolean };
type DiscordLinkedPersonDto = { handle: string | null; citizenId: number | null; displayName: string | null };
type DiscordReconciliation = "rsi_unknown" | "unlinked" | "ok" | "rank_mismatch" | "not_in_rsi_org";
type DiscordMemberDto = {
  discordUserId: string; username: string; globalName: string | null; nick: string | null; isBot: boolean;
  joinedAt: string | null; firstSeenAt: string; lastSeenAt: string; leftAt: string | null;
  rank: DiscordRankDto | null; roles: DiscordRankDto[];
  links: DiscordLinkedPersonDto[]; rsiRank: string | null;
  reconciliation: DiscordReconciliation | null;   // null when the guild is unmapped or the member is a bot
  multipleLinks: boolean };
type DiscordEventDto = {
  id: number; guildId: string | null; discordUserId: string; username: string | null; type: string;
  oldValue: string | null; newValue: string | null; occurredAt: string | null; notBefore: string | null; observedAt: string;
  submittedBy: string | null; rankChange: { from: string | null; to: string | null } | null };
type DiscordSyncDto = {
  id: number; receivedAt: string; collectedAt: string; submittedBy: string; method: string;
  declaredComplete: boolean; isComplete: boolean; isBaseline: boolean; massDepartureDetected: boolean;
  expectedCount: number | null; collectedCount: number; optedOutCount: number; unknownRoleRefCount: number;
  eventCount: number; pluginVersion: string };
type DiscordDiscrepancyDto = {
  kind: "rsi_only" | "not_in_rsi_org" | "rank_mismatch";
  handle: string | null; citizenId: number | null; discordUserId: string | null; discordName: string | null;
  discordRank: string | null; rsiRank: string | null };
type DiscordTotalsDto = { discordActive: number; discordLinked: number; rsiVisible: number | null; rsiRedacted: number | null;
  rsiHidden: number | null; rsiTotalRows: number | null; rsiCountsAt: string | null; rsiBreakdownKnown: boolean };
type DiscordDiscrepanciesDto = { orgSid: string | null; rsiOnlyAvailable: boolean; items: DiscordDiscrepancyDto[]; totals: DiscordTotalsDto | null };
type DiscordSuggestionDto = { discordUserId: string; discordName: string; matchedToken: string;
  handle: string; citizenId: number | null; displayName: string | null; confidence: "strong" | "medium" };
type DiscordMultiMemberDto = { discordUserId: string; username: string; globalName: string | null;
  guilds: { guildId: string; guildName: string; orgSid: string | null; rank: string | null }[];
  links: DiscordLinkedPersonDto[]; rsiOrgs: { sid: string; rank: string | null }[] };
type DiscordUserProfileDto = {
  accounts: { discordUserId: string; username: string; globalName: string | null;
    guilds: { guildId: string; guildName: string; orgSid: string | null; rank: string | null;
      joinedAt: string | null; leftAt: string | null; lastSeenAt: string }[] }[];
  timeline: { source: "rsi" | "discord"; type: string; at: string; notBefore: string | null; orgSid: string | null;
    guildId: string | null; guildName: string | null; oldValue: string | null; newValue: string | null }[] };
type DiscordOrgGuildDto = { guildId: string; name: string; iconHash: string | null; activeMembers: number;
  linkedMembers: number; lastSyncAt: string; lastSyncComplete: boolean };
```

**Routes of `DiscordRostersController`**:

| Method and route | Body or query | Response |
|---|---|---|
| `GET api/discord/guilds` | — | `DiscordGuildSummaryDto[]`, unmapped first, then by name |
| `GET api/discord/guilds/{guildId}` | — | `DiscordGuildDetailDto`, or 404 |
| `GET api/discord/guilds/{guildId}/members` | `status=active\|former\|all` (default `active`), `search`, `rankRoleId`, `reconciliation`, `page`, `pageSize` | `PaginatedResponse<DiscordMemberDto>` |
| `GET api/discord/guilds/{guildId}/events` | `type`, `userId`, `limit` (default 100, via `Paging.Limit`) | `DiscordEventDto[]`, newest Id first |
| `GET api/discord/guilds/{guildId}/syncs` | `limit` | `DiscordSyncDto[]` |
| `GET api/discord/guilds/{guildId}/discrepancies` | — | `DiscordDiscrepanciesDto` |
| `GET api/discord/guilds/{guildId}/suggestions` | — | `DiscordSuggestionDto[]` |
| `PUT api/discord/guilds/{guildId}/org` | `{ orgSid: string \| null }` | `DiscordGuildSummaryDto`, or 400 (unknown SID) or 403 |
| `PUT api/discord/guilds/{guildId}/roles/{roleId}` | `{ isRank: boolean, rankOrder: number \| null, rsiRankLabel: string \| null }` | `DiscordRoleDto`, or 403 or 404 |
| `POST api/discord/links` | `{ discordUserId, citizenId: number \| null, handle }` | 201 `{ entityId: number, handle: string }`, or 404 |
| `POST api/discord/link-rejections` | `{ discordUserId, citizenId: number \| null, handle }` | 201 `{ id: number }` |
| `DELETE api/discord/link-rejections/{id}` | — | 204, 403 or 404 |
| `GET api/discord/multi` | `page`, `pageSize` | `PaginatedResponse<DiscordMultiMemberDto>` |
| `GET api/users/{handle}/discord` | — | `DiscordUserProfileDto`: empty lists when there is no entity, 404 when the handle is unknown everywhere |
| `GET api/organizations/{sid}/discord` | — | `DiscordOrgGuildDto[]` |

`POST api/discord/links` and `POST api/discord/link-rejections` hold `DiscordWriteGate`. From
lot C on, `LinksController` rejects a non-snowflake value for provider `discord` with 400.

**Web (lot C)**:

- types above go in `lib/api/types.ts` ;
- `lib/api/endpoints.ts` gains `listDiscordGuilds`, `getDiscordGuild`, `getDiscordMembers`,
  `getDiscordEvents`, `getDiscordSyncs`, `getDiscordDiscrepancies`, `getDiscordSuggestions`,
  `getDiscordMulti`, `getUserDiscord` and `getOrgDiscordGuilds`, each `(ctx, …)` with
  `encodeURIComponent` on path ids ;
- `lib/validation.ts` gains `snowflakeSchema` ;
- `src/app/(public)/discord/actions.ts` (`"use server"`, `{ ok, data?, error? }`, never throws)
  holds these actions:
  - `mapGuildOrgAction(guildId, orgSid)` ;
  - `updateGuildRoleAction(guildId, roleId, isRank, rankOrder, rsiRankLabel)` ;
  - `acceptSuggestionAction(discordUserId, citizenId, handle)` ;
  - `rejectSuggestionAction(discordUserId, citizenId, handle)` ;
  - `undoRejectionAction(id)`.
  No web action erases or excludes a guild/account, resets its baseline, or authorizes departures.
  Erasure APIs remain for direct administration outside the interface.
- Pages:
  - `src/app/(public)/discord/page.tsx` ;
  - `src/app/(public)/discord/[guildId]/page.tsx`, with tab components alongside ;
  - `src/app/(public)/discord/multi/page.tsx` ;
  - a section component `src/app/(public)/users/[handle]/DiscordServersSection.tsx` ;
  - a panel `src/app/(public)/orgs/[sid]/OrgDiscordPanel.tsx`.

## 8. Lot B — plugin ↔ tracker

Folder `vencord/`. The plugin is named `"ScTracker"` and lives in `vencord/scTracker.desktop/`.
The JSON body is exactly `DiscordSyncRequest` (§ 5) in camelCase. Errors are read from the
ProblemDetails field `code` and the `Retry-After` header.

**Pure modules** in `vencord/scTracker.desktop/lib/`. They never import `@webpack/*`, `@api/*`,
`@utils/*`, `@vencord/*`, `electron` or `../collect`.

```ts
// fingerprint.ts
export function normalizeFingerprint(input: string): string | null;   // strips "sha256 Fingerprint=", ":" and spaces, upper-case, requires ^[0-9A-F]{64}$

// pinnedPost.ts (node:https only)
export type PostResult = { status: number; body: string; retryAfter: number | null;
  error?: "pin_mismatch" | "network" | "no_response_after_upload" };
export function pinnedPost(opts: { url: string; path: string; fingerprint: string;
  headers: Record<string, string>; body: string; idleTimeoutMs?: number;
  connectTimeoutMs?: number; totalTimeoutMs?: number; maxResponseBytes?: number }): Promise<PostResult>;

// payload.ts
export const MAX_BODY_BYTES = 25 * 1024 * 1024;
export const MAX_MEMBERS = 50_000;
export type SyncMethod = "member-search" | "role-members" | "cache";
export type SyncRole = { id: string; name: string; position: number; color: string | null; hoist: boolean; managed: boolean };
export type SyncMember = { userId: string; username: string; globalName: string | null; nick: string | null;
  roleIds: string[]; joinedAt: string | null; bot: boolean };
export type SyncPayload = { pluginVersion: string; collectedAt: string; collectionDurationMs: number;
  guild: { id: string; name: string; icon: string | null; memberCount: number | null };
  coverage: { method: SyncMethod; complete: boolean; expectedCount: number | null; collectedCount: number };
  roles: SyncRole[]; members: SyncMember[] };
export function buildPayload(p: SyncPayload): { ok: true; json: string; bytes: number } | { ok: false; reason: "too_many_members" | "too_large" };

// coverage.ts
export type CollectionStopReason = "exhausted" | "cursor_stalled" | "invalid_cursor" | "call_cap" | "deadline" | "error" | "aborted";
export function computeCoverage(method: SyncMethod, collected: number, expected: number | null,
  stopReason: CollectionStopReason): SyncPayload["coverage"];   // complete only for normal exhaustion, member-search and a positive count equal to expected

// pacing.ts
export class CapReachedError extends Error {}
export class AbortedError extends Error {}
export class DurationReachedError extends Error {}
export type Pacer = { beforeRest(): Promise<void>; beforeGateway(): Promise<void>; afterGatewayTimeout(): Promise<void>;
  check(): void; checkCancellation(): void; readonly callsUsed: number; readonly callsRemaining: number };
export function createPacer(o?: { restIntervalMs?: number; gatewayIntervalMs?: number; gatewayBackoffMs?: number; maxCalls?: number;
  maxDurationMs?: number; now?: () => number; sleep?: (ms: number) => Promise<void>; signal?: AbortSignal }): Pacer;   // defaults 1200 / 1000 / 30000 / 200, duration bounded below the server's 30-minute limit

// errors.ts
export type Outcome = { ok: boolean; message: string; stopBatch: boolean };
export function describeResult(r: PostResult): Outcome;   // spec § 14.1, French messages; parses body JSON for code/detail

// postArgs.ts
export type PostSyncArgs = { url: string; fingerprint: string; apiKey: string; guildId: string; body: string };
export function validatePostArgs(a: unknown): { ok: true; value: PostSyncArgs; path: string } | { ok: false; error: string };

// searchCursor.ts
export type SearchMember = { member: { user: { id: string; username: string; global_name?: string | null; bot?: boolean };
  nick?: string | null; roles: string[]; joined_at: string } };
export function nextAfter(page: SearchMember[]): { guild_joined_at: number; user_id: string } | null;
export function mergePage(seen: Map<string, SyncMember>, page: SearchMember[]): { added: number };
export function toSyncMember(m: SearchMember): SyncMember;

// chunkTracker.ts
export type ChunkLike = { guildId: string; members: { user: { id: string } }[]; notFound?: string[]; nonce?: string };
export function createChunkTracker(guildId: string, userIds: string[], nonce?: string): {
  accept(chunk: ChunkLike): void; readonly done: boolean; readonly present: Set<string>;
  readonly notFound: Set<string>; unresolved(): string[] };
```

**Native entry point**: `native.ts` exports
`postSync(_e: IpcMainInvokeEvent, args: unknown): Promise<PostResult>`. It validates the
arguments with `validatePostArgs`, then calls `pinnedPost` with
`path = /ingest/discord/guilds/{guildId}/syncs` and the headers `x-api-key` and
`content-type: application/json`.
An argument refusal is a local `PostResult` with status 400, a JSON ProblemDetails
body containing the validation detail, `retryAfter: null` and no transport error.
It must never open a connection.

**Glue** (`collect/*.ts`, `index.tsx`, `settings.tsx`): these files use `@webpack/common`,
`@api/DataStore`, `@api/ContextMenu` and `@utils/types`. They are type-checked only inside
the Vencord tree in CI.

**Collection invariants (review corrections)**:

- The plugin persists only connection settings, selected guild IDs and timer preferences. It has no SQLite
  database, saved roster, history or durable upload queue. Collection bodies live only in
  the running job. UI results live only in renderer session memory and clear on stop/start;
  startup removes legacy `lastResults` from Vencord settings. The tracker owns all history.
- The opt-in automatic timer starts after a full configured interval (60 minutes by default,
  10 to 10,080 minutes). It uses the latest selection and the shared runner, skips busy
  occurrences, rearms after completion, and never queues missed sends. Disabling the plugin
  clears the timer and settings listener. The visible stop button also disables automatic
  mode. Next-run dates are session-only; restarting waits a fresh interval.
- Context-menu and per-guild manual sends use `runner.run([guildId], { requireTracked: false })`:
  the target needs no checkbox and its selection/timer preferences remain untouched. Batch
  and automatic sends keep the default selection checks, including immediately before IPC.
  Both modes share validation, cancellation, fresh collection and the single active-job gate.
- An abnormal stop always sets `coverage.complete = false`, including a stuck cursor
  whose collected count happens to match the advertised total.
- Cancellation is checked after every awaited response and immediately before IPC.
  A cancelled collection produces no upload; an upload already started has a distinct UI state.
- `Pacer.check()` checks cancellation and deadline during collection. After deadline finalization,
  `checkCancellation()` checks the signal alone before IPC so a valid partial upload can proceed.
- Gateway refresh passes and checks a per-batch nonce. Three idempotent propagation patches
  follow the pinned upstream ImplicitRelationships paths. No correlated first batch means
  no upload; a later missing batch preserves only already correlated members as partial.
  Subscription time alone cannot correlate delayed chunks. Real Discord acceptance is still required.
- The DataStore connection record binds the key to a canonical HTTPS origin and certificate
  fingerprint. Cloud Sync or settings imports cannot redirect it; mismatch requires an explicit
  local save. Configuration is checked again after collection before IPC. Legacy unbound keys
  are shown locally for migration but never used for sending until saved.
- Role candidate enumeration reserves calls for refreshing its accumulated IDs, which survive
  a role endpoint failure or budget stop. Empty collections are never uploaded. Name bounds
  count Unicode code points. Index retries wait at least five seconds and reset after a valid page.
- The shared collection duration budget includes fallback strategies and retries, and stops
  before the server's 30-minute bound. Do not clamp the measured duration to disguise overruns.
- Transport uses connection and total deadlines, a bounded response, and the existing
  inactivity timeout. No application bytes leave before the certificate pin matches.
- Local pure-module checks use the plugin package's pinned manager; the Vencord checkout
  uses its own pinned manager. At the reference commit these are pnpm 10.27 and 11.9 respectively.

## 9. Decisions recorded while writing the plans

These come from the lot A tasks already written. They are binding for every other task.

**Auth (A1–A2)**

- `ApiKeyScopes.MaxDiscordIngestLifetimeDays = 365`. An expiry sent to `POST /api/api-keys` is
  converted to UTC before it is checked. Key validation errors are a plain `ValidationException`
  with no code.
- `DiscordIngestAuth.IngestScope` is defined as `ApiKeyScopes.DiscordIngest`.
- The `Smart` selector uses `Request.Path.StartsWithSegments(PathPrefix,
  StringComparison.OrdinalIgnoreCase)`.
- The ingest principal carries exactly three claims (owner id, owner name,
  `scope=discord:ingest`) and never a role.
- API-key `LastUsedAt` is best-effort, monotone telemetry: validation reads credentials
  without tracking, then attempts a conditional update. SQLite BUSY/LOCKED while updating
  that timestamp does not turn an otherwise live credential into a 500 before the write gate.
- Failure messages:
  - the `ApiKey` scheme returns `Fail("Scoped key")` for any scoped key ;
  - `DiscordIngestKey` returns `"Invalid API key"` (unknown, revoked or expired key, or the
    static admin key), `"Not a discord:ingest key"` (full key) and `"Account is banned"` ;
  - no key at all gives `NoResult`.
- `AuthorizationTests.IngestRoutes` whitelists `"POST /api/ingest/discord/guilds/x/syncs"`. The
  ingest route template must therefore put **no constraint** on `{guildId}`.

**Rate limiting (A4)**

- The nested settings type is `RateLimitSettings.DiscordIngestLimit`, exposed as
  `RateLimitSettings.DiscordIngest`.
- `RateLimitingExtensions.DiscordIngestPartitionKey(HttpContext)` is public.
- Every 429 now carries `Retry-After`.
- Scoped ingest principals use a separate global partition from the owner's JWT/full key.
  Exhausting upload requests therefore cannot spend the site's key-revocation budget.

**Errors (A3)**

- A 413 ProblemDetails has neither `detail` nor `code`.
- Giving `ConflictException` and `ValidationException` an optional `code` breaks reflection
  that calls `Activator.CreateInstance(type, message)`. Construct them directly instead.

**Options and settings panel (A13)**

- `DiscordOptions` is bound once, in `AddApiServices` (A13). Consumers read
  `IOptions<DiscordOptions>().Value`.
- `DiscordIngestConfigDto` lives in `Dtos/Discord/DiscordDtos.cs`. Blank settings are returned as
  `null`, and the fingerprint is not normalised by the API.
- `ApiFactory` exposes `DiscordIngestPublicUrl` and `DiscordIngestCertificateSha256`, with the
  matching `COLLECTOR_API_Discord__Ingest__*` environment variables.
- `api.env` stores the fingerprint as the bare colon-separated hex that follows `Fingerprint=`.
  `normalizeFingerprint` accepts both that form and the full openssl line.
- The settings panel lists all of the user's keys, full-access and revoked ones included, and
  refreshes with `router.refresh()`.

**Left to other lots**

- Lot B: the root README "Dépôt" line for `vencord/`, and the CI `vencord` job.
- Lot C: the new pages in the web README, and `smoke.spec.ts`.

**nginx**: `limit_req_zone` and `limit_conn_zone` sit at the top of `sc-tracker.conf` (http
level, next to `sc_login`), not inside the `server` block.

**Vitest**: never write `beforeEach(() => mock.mockReset())`. The arrow returns the mock, and
vitest runs a returned function as a teardown. Always use a braced body.
