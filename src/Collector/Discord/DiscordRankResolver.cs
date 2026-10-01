using Collector.Models;

namespace Collector.Discord;

/// <summary>A member's rank: the Discord role that stands for it, with its current name and colour.</summary>
public sealed record RankRole(string RoleId, string Name, string? Color);

/// <summary>
/// Deduces a member's rank on read (spec § 9.5). Among the member's roles that are ranks and
/// are not deleted, the first in rank order wins: RankOrder descending, then Position
/// descending, then RoleId (ordinal). Nothing is stored, so changing the rank configuration
/// re-reads the whole history without rewriting it.
/// </summary>
public static class DiscordRankResolver
{
    /// <summary>
    /// The member's rank, or null when none of <paramref name="memberRoleIds"/> is a live rank
    /// role of the guild. Ids missing from <paramref name="guildRoles"/> are ignored.
    /// </summary>
    public static RankRole? Resolve(IEnumerable<string> memberRoleIds, IReadOnlyDictionary<string, DiscordRole> guildRoles)
    {
        ArgumentNullException.ThrowIfNull(memberRoleIds);
        ArgumentNullException.ThrowIfNull(guildRoles);

        DiscordRole? best = null;
        foreach (var roleId in memberRoleIds)
        {
            if (!guildRoles.TryGetValue(roleId, out var role) || !role.IsRank || role.DeletedAt is not null) continue;
            if (best is null || Compare(role, best) < 0) best = role;
        }
        return best is null ? null : new RankRole(best.RoleId, best.Name, best.Color);
    }

    /// <summary>
    /// Rank order of two roles: negative when <paramref name="a"/> ranks above
    /// <paramref name="b"/>. RankOrder descending (a rank without an order comes last), then
    /// Position descending, then RoleId ordinal. Also sorts rank lists (rank distribution).
    /// </summary>
    public static int Compare(DiscordRole a, DiscordRole b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        var byOrder = Nullable.Compare(b.RankOrder, a.RankOrder);
        if (byOrder != 0) return byOrder;
        var byPosition = b.Position.CompareTo(a.Position);
        return byPosition != 0 ? byPosition : string.CompareOrdinal(a.RoleId, b.RoleId);
    }
}
