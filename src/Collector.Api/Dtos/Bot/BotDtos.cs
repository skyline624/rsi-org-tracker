namespace Collector.Api.Dtos.Bot;

// Compact answers for the Discord bot (spec § 5.2); dates in UTC.
public sealed record BotSearchDto(IReadOnlyList<BotOrgHitDto> Orgs, IReadOnlyList<BotPlayerHitDto> Players);
public sealed record BotOrgHitDto(string Sid, string Name, int MembersCount);
public sealed record BotPlayerHitDto(string Handle, string? DisplayName);

public sealed record BotMembershipDto(string Sid, string? Name, string? Rank, int? Stars, DateTime? Since, DateTime LastSeen, bool Active);
public sealed record BotPlayerDto(
    string Handle, string? DisplayName, int? CitizenId, DateTime? Enlisted, string? Location,
    bool ProfileRead, IReadOnlyList<BotMembershipDto> CurrentOrgs, DateTime? LastSeen);
public sealed record BotHandleDto(string Handle, DateTime FirstSeen, DateTime LastSeen);
public sealed record BotEventDto(DateTime At, string Type, string? OrgSid, string? Old, string? New);
public sealed record BotHistoryDto(
    string Handle, IReadOnlyList<BotMembershipDto> Orgs, IReadOnlyList<BotHandleDto> Handles, IReadOnlyList<BotEventDto> Events);

public sealed record BotCountsDto(int Total, int? Visible, int? Redacted, int? Hidden, DateTime At);
public sealed record BotTrendDto(int From, int To);
public sealed record BotOrgDto(
    string Sid, string Name, string? Archetype, string? Lang, bool? Recruiting, bool? Roleplay, int MembersCount,
    BotCountsDto? Counts, BotTrendDto? Trend30d, DateTime? MembersReadAt);
public sealed record BotMemberDto(string Handle, string? DisplayName, string? Rank, int? Stars, DateTime? Since);
public sealed record BotMembersPageDto(string Sid, int Page, int PageSize, int Total, IReadOnlyList<BotMemberDto> Items);
public sealed record BotMovementDto(string Handle, DateTime At);
public sealed record BotMovementsDto(string Sid, int Days, IReadOnlyList<BotMovementDto> Joined, IReadOnlyList<BotMovementDto> Left, bool Truncated);
