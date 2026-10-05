namespace Collector.Api.Dtos.Discord;

/// <summary>
/// Statuses of an active, non-bot Discord member against the active RSI roster of the org
/// the guild is mapped to (spec § 10.2).
/// </summary>
public static class DiscordReconciliationStatus
{
    /// <summary>The org has no active organization_members row: its roster was never collected.</summary>
    public const string RsiUnknown = "rsi_unknown";

    /// <summary>Nobody is linked to the member's Discord id.</summary>
    public const string Unlinked = "unlinked";

    /// <summary>A linked person is in the roster with a coherent rank, or the Discord rank has no RSI equivalent.</summary>
    public const string Ok = "ok";

    /// <summary>A linked person is in the roster, but none with a coherent rank.</summary>
    public const string RankMismatch = "rank_mismatch";

    /// <summary>No linked person is in the roster: absent from the RSI org, or hidden there.</summary>
    public const string NotInRsiOrg = "not_in_rsi_org";

    public static bool IsValid(string? value) => value is RsiUnknown or Unlinked or Ok or RankMismatch or NotInRsiOrg;
}

/// <summary>Kinds of <see cref="DiscordDiscrepancyDto"/>.</summary>
public static class DiscordDiscrepancyKinds
{
    /// <summary>In the active RSI roster, with no linked Discord id active on the guild.</summary>
    public const string RsiOnly = "rsi_only";

    public const string NotInRsiOrg = DiscordReconciliationStatus.NotInRsiOrg;
    public const string RankMismatch = DiscordReconciliationStatus.RankMismatch;
}

/// <summary>A tracked person linked to a Discord account through an entity_links "discord" row.</summary>
public sealed class DiscordLinkedPersonDto
{
    public string? Handle { get; set; }
    public int? CitizenId { get; set; }
    public string? DisplayName { get; set; }
}

/// <summary>One gap between a guild and the RSI roster of its org.</summary>
public sealed class DiscordDiscrepancyDto
{
    /// <summary><see cref="DiscordDiscrepancyKinds"/> value.</summary>
    public string Kind { get; set; } = null!;

    /// <summary>The RSI person concerned: the linked person, or the roster member for rsi_only.</summary>
    public string? Handle { get; set; }

    public int? CitizenId { get; set; }

    /// <summary>Null for rsi_only.</summary>
    public string? DiscordUserId { get; set; }

    /// <summary>Nick, else global name, else username. Null for rsi_only.</summary>
    public string? DiscordName { get; set; }

    /// <summary>Name of the member's Discord rank.</summary>
    public string? DiscordRank { get; set; }

    /// <summary>Rank in the RSI roster, when the person is in it.</summary>
    public string? RsiRank { get; set; }
}

/// <summary>Headcounts of a guild and of its org's RSI roster (latest org_member_counts row only).</summary>
public sealed class DiscordTotalsDto
{
    /// <summary>Active, non-bot members.</summary>
    public int DiscordActive { get; set; }

    /// <summary>Active, non-bot members linked to at least one person.</summary>
    public int DiscordLinked { get; set; }

    public int? RsiVisible { get; set; }
    public int? RsiRedacted { get; set; }
    public int? RsiHidden { get; set; }
    public int? RsiTotalRows { get; set; }
    public DateTime? RsiCountsAt { get; set; }

    /// <summary>False when the latest reading has no breakdown: show RsiTotalRows alone ("répartition inconnue").</summary>
    public bool RsiBreakdownKnown { get; set; }
}

/// <summary>Answer of GET api/discord/guilds/{guildId}/discrepancies.</summary>
public sealed class DiscordDiscrepanciesDto
{
    /// <summary>Null for an unmapped guild, which has no items and no totals.</summary>
    public string? OrgSid { get; set; }

    /// <summary>False until a complete sync proves absences: rsi_only items are then not computed.</summary>
    public bool RsiOnlyAvailable { get; set; }

    public IReadOnlyList<DiscordDiscrepancyDto> Items { get; set; } = [];
    public DiscordTotalsDto? Totals { get; set; }
}

/// <summary>A guild a multi-member is active in.</summary>
public sealed class DiscordMultiGuildDto
{
    public string GuildId { get; set; } = null!;
    public string GuildName { get; set; } = null!;
    public string? OrgSid { get; set; }

    /// <summary>Name of the member's rank in that guild.</summary>
    public string? Rank { get; set; }
}

/// <summary>An RSI org one of the linked people is an active member of.</summary>
public sealed class DiscordRsiOrgDto
{
    public string Sid { get; set; } = null!;
    public string? Rank { get; set; }
}

/// <summary>A non-bot account active in at least two tracked guilds (spec § 10.3).</summary>
public sealed class DiscordMultiMemberDto
{
    public string DiscordUserId { get; set; } = null!;
    public string Username { get; set; } = null!;
    public string? GlobalName { get; set; }
    public IReadOnlyList<DiscordMultiGuildDto> Guilds { get; set; } = [];
    public IReadOnlyList<DiscordLinkedPersonDto> Links { get; set; } = [];

    /// <summary>Union of the active RSI orgs of every linked person, with their rank there.</summary>
    public IReadOnlyList<DiscordRsiOrgDto> RsiOrgs { get; set; } = [];
}

/// <summary>A Discord role as shown next to a member: its id, current name and colour.</summary>
public sealed class DiscordRankDto
{
    public string RoleId { get; set; } = null!;
    public string Name { get; set; } = null!;
    public string? Color { get; set; }
}

/// <summary>Number of active human members whose rank is this role.</summary>
public sealed class DiscordRankCountDto
{
    public string RoleId { get; set; } = null!;
    public string Name { get; set; } = null!;
    public string? Color { get; set; }
    public int Count { get; set; }
}

/// <summary>The last accepted sync of a guild.</summary>
public sealed class DiscordLastSyncDto
{
    public DateTime ReceivedAt { get; set; }
    public bool IsComplete { get; set; }
    public string Method { get; set; } = null!;
    public string SubmittedBy { get; set; } = null!;
    public bool MassDepartureDetected { get; set; }
}

/// <summary>A tracked guild, as listed in the DISCORD tab.</summary>
public class DiscordGuildSummaryDto
{
    public string GuildId { get; set; } = null!;
    public string Name { get; set; } = null!;
    public string? IconHash { get; set; }
    public string? OrgSid { get; set; }

    /// <summary>Latest name of the org, when it is known to the tracker.</summary>
    public string? OrgName { get; set; }

    /// <summary>The guild's responsible: the user who mapped it (spec § 11).</summary>
    public string? OrgMappedBy { get; set; }

    /// <summary>Active, non-bot members.</summary>
    public int ActiveMembers { get; set; }

    /// <summary>Live ranks held by at least one active human member, in rank order.</summary>
    public IReadOnlyList<DiscordRankCountDto> RankDistribution { get; set; } = [];

    public DiscordLastSyncDto? LastSync { get; set; }
    public DateTime? LastCompleteSyncAt { get; set; }

    /// <summary>
    /// The corpo proposed from the members' tags (spec § 10.1); null when the guild is mapped
    /// or when no known tag dominates.
    /// </summary>
    public DiscordDetectedOrgDto? DetectedOrg { get; set; }
}

/// <summary>A corpo proposed for an unmapped guild, from the tags its active members carry.</summary>
public sealed class DiscordDetectedOrgDto
{
    public string Sid { get; set; } = null!;

    /// <summary>Latest name of the org.</summary>
    public string Name { get; set; } = null!;

    /// <summary>Active, non-bot members carrying the tag.</summary>
    public int Members { get; set; }

    /// <summary>Active, non-bot members carrying at least one known corpo tag.</summary>
    public int TaggedMembers { get; set; }
}

/// <summary>A role of a guild with its rank configuration.</summary>
public sealed class DiscordRoleDto
{
    public string RoleId { get; set; } = null!;
    public string Name { get; set; } = null!;
    public int Position { get; set; }
    public string? Color { get; set; }
    public bool Hoist { get; set; }
    public bool Managed { get; set; }
    public bool IsRank { get; set; }
    public int? RankOrder { get; set; }
    public string? RsiRankLabel { get; set; }

    /// <summary>The role was missing from a later sync.</summary>
    public bool Deleted { get; set; }

    /// <summary>Active, non-bot members holding the role.</summary>
    public int MemberCount { get; set; }
}

/// <summary>A guild with its roles, the RSI ranks of its org and whether the caller may configure it.</summary>
public sealed class DiscordGuildDetailDto : DiscordGuildSummaryDto
{
    /// <summary>Every role, deleted ones last.</summary>
    public IReadOnlyList<DiscordRoleDto> Roles { get; set; } = [];

    /// <summary>Distinct, trimmed ranks of the org's active roster, highest stars first.</summary>
    public IReadOnlyList<string> RsiRanks { get; set; } = [];

    /// <summary>Unmapped, or the caller is its responsible or an admin.</summary>
    public bool CanEdit { get; set; }
}

/// <summary>A member of a guild with rank, links and reconciliation status.</summary>
public sealed class DiscordMemberDto
{
    public string DiscordUserId { get; set; } = null!;
    public string Username { get; set; } = null!;
    public string? GlobalName { get; set; }
    public string? Nick { get; set; }
    public bool IsBot { get; set; }
    public DateTime? JoinedAt { get; set; }
    public DateTime FirstSeenAt { get; set; }
    public DateTime LastSeenAt { get; set; }
    public DateTime? LeftAt { get; set; }
    public DiscordRankDto? Rank { get; set; }

    /// <summary>Live roles of the member, by position descending.</summary>
    public IReadOnlyList<DiscordRankDto> Roles { get; set; } = [];

    public IReadOnlyList<DiscordLinkedPersonDto> Links { get; set; } = [];
    public string? RsiRank { get; set; }

    /// <summary><see cref="DiscordReconciliationStatus"/> value; null when the guild is unmapped or the member is a bot.</summary>
    public string? Reconciliation { get; set; }

    public bool MultipleLinks { get; set; }
}

/// <summary>"Rang : X → Y" of a roles_changed event that moved the member's rank.</summary>
public sealed class DiscordRankChangeDto
{
    public string? From { get; set; }
    public string? To { get; set; }
}

/// <summary>One event of a guild's history.</summary>
public sealed class DiscordEventDto
{
    public long Id { get; set; }

    /// <summary>Null for an account-level event (username or global name change).</summary>
    public string? GuildId { get; set; }

    public string DiscordUserId { get; set; } = null!;

    /// <summary>The account's current username.</summary>
    public string? Username { get; set; }

    public string Type { get; set; } = null!;
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    public DateTime? OccurredAt { get; set; }
    public DateTime? NotBefore { get; set; }
    public DateTime ObservedAt { get; set; }

    /// <summary>Who sent the source sync; null once that sync left the log.</summary>
    public string? SubmittedBy { get; set; }

    public DiscordRankChangeDto? RankChange { get; set; }
}

/// <summary>One accepted sync of a guild.</summary>
public sealed class DiscordSyncDto
{
    public long Id { get; set; }
    public DateTime ReceivedAt { get; set; }
    public DateTime CollectedAt { get; set; }
    public string SubmittedBy { get; set; } = null!;
    public string Method { get; set; } = null!;
    public bool DeclaredComplete { get; set; }
    public bool IsComplete { get; set; }
    public bool IsBaseline { get; set; }
    public bool MassDepartureDetected { get; set; }
    public int? ExpectedCount { get; set; }
    public int CollectedCount { get; set; }
    public int OptedOutCount { get; set; }
    public int UnknownRoleRefCount { get; set; }
    public int EventCount { get; set; }
    public string PluginVersion { get; set; } = null!;
}

/// <summary>A guild a linked Discord account is or was a member of.</summary>
public sealed class DiscordProfileGuildDto
{
    public string GuildId { get; set; } = null!;
    public string GuildName { get; set; } = null!;
    public string? OrgSid { get; set; }

    /// <summary>Current rank, or the last one for a departed member.</summary>
    public string? Rank { get; set; }

    public DateTime? JoinedAt { get; set; }
    public DateTime? LeftAt { get; set; }
    public DateTime LastSeenAt { get; set; }
}

/// <summary>A Discord account linked to the person, with its guilds (current first).</summary>
public sealed class DiscordProfileAccountDto
{
    public string DiscordUserId { get; set; } = null!;
    public string Username { get; set; } = null!;
    public string? GlobalName { get; set; }
    public IReadOnlyList<DiscordProfileGuildDto> Guilds { get; set; } = [];
}

/// <summary>Sources of <see cref="DiscordTimelineEntryDto"/>.</summary>
public static class DiscordTimelineSources
{
    public const string Rsi = "rsi";
    public const string Discord = "discord";
}

/// <summary>One entry of the combined RSI + Discord timeline of a citizen (spec § 10.4).</summary>
public sealed class DiscordTimelineEntryDto
{
    /// <summary><see cref="DiscordTimelineSources"/> value.</summary>
    public string Source { get; set; } = null!;

    /// <summary>change_events.ChangeType or discord_member_events.Type.</summary>
    public string Type { get; set; } = null!;

    /// <summary>Exact date when known, else the date it was observed.</summary>
    public DateTime At { get; set; }

    /// <summary>Earliest possible date, for a Discord event whose exact date is unknown.</summary>
    public DateTime? NotBefore { get; set; }

    public string? OrgSid { get; set; }
    public string? GuildId { get; set; }
    public string? GuildName { get; set; }
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
}

/// <summary>Answer of GET api/users/{handle}/discord: empty lists for a person without Discord data.</summary>
public sealed class DiscordUserProfileDto
{
    public IReadOnlyList<DiscordProfileAccountDto> Accounts { get; set; } = [];

    /// <summary>At most 100 entries, newest first.</summary>
    public IReadOnlyList<DiscordTimelineEntryDto> Timeline { get; set; } = [];
}

/// <summary>A guild mapped to an org, for the org page's DISCORD panel.</summary>
public sealed class DiscordOrgGuildDto
{
    public string GuildId { get; set; } = null!;
    public string Name { get; set; } = null!;
    public string? IconHash { get; set; }

    /// <summary>Active, non-bot members.</summary>
    public int ActiveMembers { get; set; }

    /// <summary>Active, non-bot members linked to at least one person.</summary>
    public int LinkedMembers { get; set; }

    public DateTime LastSyncAt { get; set; }

    /// <summary>The last accepted sync was complete.</summary>
    public bool LastSyncComplete { get; set; }
}


/// <summary>How sure a link suggestion is (spec § 10.1).</summary>
public static class DiscordSuggestionConfidence
{
    /// <summary>
    /// The token equals the handle of an active member of the org the guild is mapped to, or
    /// of the org the member's own corpo tag names (<see cref="DiscordStrongVia"/>).
    /// </summary>
    public const string Strong = "strong";

    /// <summary>The token equals a current or former RSI handle.</summary>
    public const string Medium = "medium";
}

/// <summary>What made a suggestion strong (spec § 10.1).</summary>
public static class DiscordStrongVia
{
    /// <summary>The handle is an active member of the org the guild is mapped to.</summary>
    public const string Server = "server";

    /// <summary>The handle is an active member of the org the member's own corpo tag names.</summary>
    public const string Tag = "tag";
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

    /// <summary><see cref="DiscordStrongVia"/> value for a strong suggestion; null for a medium one.</summary>
    public string? StrongVia { get; set; }

    /// <summary>The corpo whose active roster holds the handle; null for a medium suggestion.</summary>
    public string? StrongOrgSid { get; set; }
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


/// <summary>Body of PUT api/discord/guilds/{guildId}/org: an RSI SID, or null to unmap the guild.</summary>
public sealed record MapDiscordGuildOrgRequest(string? OrgSid);

/// <summary>Body of PUT api/discord/guilds/{guildId}/roles/{roleId}: rank configuration of one role.</summary>
public sealed record UpdateDiscordRoleRequest(bool IsRank, int? RankOrder, string? RsiRankLabel);
