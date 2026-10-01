namespace Collector.Api.Dtos.Discord;

/// <summary>
/// Body of <c>POST api/ingest/discord/guilds/{guildId}/syncs</c>, sent by the Vencord plugin
/// (spec § 7.1). Snowflakes travel as strings, since JavaScript numbers cannot hold them.
/// <c>CollectedAt</c> is the plugin's clock and only informative. Unknown JSON fields are
/// ignored, so a later plugin may send more than this.
/// </summary>
public sealed record DiscordSyncRequest(
    string PluginVersion, DateTimeOffset CollectedAt, long CollectionDurationMs,
    DiscordSyncGuild Guild, DiscordSyncCoverage Coverage,
    IReadOnlyList<DiscordSyncRole> Roles, IReadOnlyList<DiscordSyncMember> Members);

/// <summary>The guild as the client saw it; <c>Icon</c> is Discord's icon hash.</summary>
public sealed record DiscordSyncGuild(string Id, string Name, string? Icon, int? MemberCount);

/// <summary>
/// How the members were collected, and whether the plugin believes the list is whole. The
/// server recomputes completeness and never trusts <c>Complete</c> alone.
/// </summary>
public sealed record DiscordSyncCoverage(string Method, bool Complete, int? ExpectedCount, int CollectedCount);

/// <summary>A guild role. The plugin always sends the whole list, without @everyone.</summary>
public sealed record DiscordSyncRole(string Id, string Name, int Position, string? Color, bool Hoist, bool Managed);

/// <summary>One member, read from a member-search response or a gateway chunk of this collection.</summary>
public sealed record DiscordSyncMember(
    string UserId, string Username, string? GlobalName, string? Nick,
    IReadOnlyList<string> RoleIds, DateTimeOffset? JoinedAt, bool Bot);

/// <summary>Events created by one sync, by kind. <c>NameChanged</c> covers username and global name.</summary>
public sealed class DiscordSyncEventCountsDto
{
    public int Joined { get; set; }
    public int Left { get; set; }
    public int Rejoined { get; set; }
    public int RolesChanged { get; set; }
    public int NickChanged { get; set; }
    public int NameChanged { get; set; }
}

/// <summary>Answer to an accepted sync (spec § 7.2). <c>OrgSid</c> is null for a guild tied to no org.</summary>
public sealed class DiscordSyncResponseDto
{
    public long SyncId { get; set; }
    public bool IsBaseline { get; set; }
    public bool IsComplete { get; set; }
    public bool MassDepartureDetected { get; set; }
    /// <summary>Compatibility with installed older plugins: departures are never blocked.</summary>
    public bool DepartureGuardTripped => false;
    public string? OrgSid { get; set; }
    public int MembersReceived { get; set; }
    public int MembersOptedOut { get; set; }
    public int UnknownRoleRefs { get; set; }
    public DiscordSyncEventCountsDto Events { get; set; } = new();
}
