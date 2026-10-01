namespace Collector.Discord;

/// <summary>A role as the sync sent it, validated.</summary>
public sealed record NormalizedRole(string RoleId, string Name, int Position, string? Color, bool Hoist, bool Managed);

/// <summary>A member as the sync sent it, validated and normalized (spec § 7.3).</summary>
public sealed record NormalizedMember(
    string UserId, string Username, string? GlobalName, string? Nick,
    IReadOnlyList<string> RoleIds,   // sorted ordinal, distinct, only ids present in NormalizedSync.Roles
    DateTime? JoinedAt,              // UTC, truncated to the second
    bool IsBot);

/// <summary>A validated sync request, ready for the diff.</summary>
public sealed record NormalizedSync(
    string GuildId, string GuildName, string? IconHash, int? MemberCount,
    string Method, bool DeclaredComplete,
    bool IsComplete,                 // DeclaredComplete && Method == member-search && raw Members.Count == ExpectedCount
    int? ExpectedCount, int CollectedCount /* raw members count */,
    string PluginVersion, DateTime DeclaredCollectedAt /* UTC */, TimeSpan CollectionDuration,
    IReadOnlyList<NormalizedRole> Roles, IReadOnlyList<NormalizedMember> Members,
    int UnknownRoleRefCount);

/// <summary>The stored discord_guilds fields the diff needs.</summary>
public sealed record GuildSnapshot(DateTime FirstSyncAt, DateTime? LastCompleteSyncAt, bool BaselinePending = false);

/// <summary>A stored role: its current name (the name "of the time" for event values) and whether it is deleted.</summary>
public sealed record RoleSnapshot(string RoleId, string Name, bool IsDeleted);

/// <summary>A stored discord_members row, with the Id of its latest "left" event.</summary>
public sealed record MemberSnapshot(
    string UserId, string? Nick, IReadOnlyList<string> RoleIds, DateTime? JoinedAt,
    DateTime LastSeenAt, DateTime? LeftAt, long? LastLeftEventId);

/// <summary>A stored discord_accounts row.</summary>
public sealed record AccountSnapshot(string UserId, string Username, string? GlobalName, bool IsBot = false, DateTime? LastSeenAt = null);

/// <summary>Everything stored about a server before a sync is applied.</summary>
public sealed record RosterSnapshot(
    GuildSnapshot? Guild,                                   // null => this sync is the baseline
    IReadOnlyDictionary<string, RoleSnapshot> Roles,        // every stored role of the guild, deleted ones included
    IReadOnlyDictionary<string, MemberSnapshot> Members,    // every stored member row of the guild (active and left)
    IReadOnlyDictionary<string, AccountSnapshot> Accounts); // stored accounts among the payload's user ids

/// <summary>A discord_member_events row to write; the repository adds the SyncId.</summary>
public sealed record PlannedEvent(
    string? GuildId, string UserId, string Type, string? OldValue, string? NewValue,
    DateTime? OccurredAt, DateTime? NotBefore, DateTime ObservedAt);

/// <summary>Target state of a discord_members row (insert or update).</summary>
public sealed record MemberWrite(string UserId, string? Nick, IReadOnlyList<string> RoleIds, DateTime? JoinedAt, DateTime? LeftAt);

/// <summary>Target state of a discord_accounts row (insert or update).</summary>
public sealed record AccountWrite(string UserId, string Username, string? GlobalName, bool IsBot);

/// <summary>What a sync changes: computed in memory by <see cref="DiscordRosterDiff"/>, written by the repository.</summary>
public sealed record RosterPlan(
    bool IsBaseline,
    bool IsComplete,
    bool MassDepartureDetected,             // informational: every observed departure is recorded
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
