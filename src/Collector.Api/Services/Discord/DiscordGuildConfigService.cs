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
