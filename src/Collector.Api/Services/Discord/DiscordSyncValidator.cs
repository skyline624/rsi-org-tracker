using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Collector.Api.Dtos.Discord;
using Collector.Api.Errors;
using Collector.Discord;
using Collector.Models;

namespace Collector.Api.Services.Discord;

/// <summary>
/// Checks a plugin sync against spec § 7.3 and turns it into the <see cref="NormalizedSync"/>
/// the diff works on. SQLite does not enforce TEXT(n), so every bound is checked here, and a
/// failure is a 400 before anything is written. Text lengths are counted in Unicode scalar
/// values, the unit Discord counts in: a nick of 32 emoji is valid although it takes 64 UTF-16
/// units. Nothing here reads a clock, because the server dates a sync itself.
/// </summary>
public static class DiscordSyncValidator
{
    public const int MaxGuildNameLength = 100;
    public const int MaxRoleNameLength = 100;

    /// <summary>Bound of username, global name and nick.</summary>
    public const int MaxNameLength = 32;

    public const int MaxPluginVersionLength = 20;

    /// <summary>Bound of <c>memberCount</c> and <c>expectedCount</c>.</summary>
    public const int MaxCount = 1_000_000;

    public const int MaxRolePosition = 1_000;

    /// <summary>30 minutes.</summary>
    public const long MaxCollectionDurationMs = 1_800_000;

    public const int MaxRoles = 250;
    public const int MaxMembers = 50_000;
    public const int MaxRoleIdsPerMember = 250;

    // \z rather than $: in .NET, $ also matches before a final "\n".
    private static readonly Regex IconHash = new(@"^(a_)?[0-9a-f]{32}\z", RegexOptions.Compiled);
    private static readonly Regex HexColor = new(@"^#[0-9a-fA-F]{6}\z", RegexOptions.Compiled);

    /// <summary>
    /// Validates <paramref name="r"/> for the guild of the URL and normalises it:
    /// <list type="bullet">
    /// <item>dates are converted to UTC, and <c>JoinedAt</c> is truncated to the second;</item>
    /// <item>nick and global name are trimmed, and a blank value reads as null;</item>
    /// <item>role ids are sorted and de-duplicated;</item>
    /// <item>references to roles absent from the payload are dropped and counted.</item>
    /// </list>
    /// Completeness is recomputed on the raw member count, before opt-outs are removed.
    /// </summary>
    /// <exception cref="ValidationException">
    /// Code <c>invalid_sync</c>, or <c>empty_complete_sync</c> for a sync declared complete
    /// without any member.
    /// </exception>
    public static NormalizedSync Normalize(string routeGuildId, DiscordSyncRequest r)
    {
        if (!DiscordSnowflake.IsValid(routeGuildId)) Fail("guildId (URL) : snowflake attendu.");
        if (r is null) Fail("Corps de requête absent.");
        if (r.Guild is null) Fail("guild est obligatoire.");
        if (r.Coverage is null) Fail("coverage est obligatoire.");
        if (r.Roles is null) Fail("roles est obligatoire.");
        if (r.Members is null) Fail("members est obligatoire.");

        var guild = r.Guild;
        if (!DiscordSnowflake.IsValid(guild.Id)) Fail("guild.id : snowflake attendu.");
        if (guild.Id != routeGuildId) Fail("guild.id ne correspond pas au serveur de l'URL.");
        RequireText(guild.Name, MaxGuildNameLength, "guild.name");
        if (guild.Icon is not null && !IconHash.IsMatch(guild.Icon)) Fail("guild.icon : hash d'icône Discord attendu.");
        RequireCount(guild.MemberCount, "guild.memberCount");

        RequireText(r.PluginVersion, MaxPluginVersionLength, "pluginVersion");
        if (r.CollectionDurationMs is < 0 or > MaxCollectionDurationMs)
            Fail($"collectionDurationMs : entre 0 et {MaxCollectionDurationMs}.");

        var coverage = r.Coverage;
        if (!DiscordSyncMethods.IsValid(coverage.Method))
            Fail("coverage.method : member-search, role-members ou cache.");
        RequireCount(coverage.ExpectedCount, "coverage.expectedCount");

        if (r.Roles.Count > MaxRoles) Fail($"roles : {MaxRoles} au plus.");
        if (r.Members.Count > MaxMembers) Fail($"members : {MaxMembers} au plus.");
        if (coverage.Complete && r.Members.Count == 0)
            throw new ValidationException("Envoi déclaré complet sans aucun membre.", DiscordErrorCodes.EmptyCompleteSync);

        var roles = NormalizeRoles(r.Roles);
        var knownRoleIds = roles.Select(role => role.RoleId).ToHashSet(StringComparer.Ordinal);
        var members = new List<NormalizedMember>(r.Members.Count);
        var userIds = new HashSet<string>(StringComparer.Ordinal);
        var unknownRoleRefs = 0;
        for (var i = 0; i < r.Members.Count; i++)
        {
            var member = NormalizeMember(r.Members[i], i, knownRoleIds, out var unknown);
            if (!userIds.Add(member.UserId)) Fail($"members[{i}].userId : membre en double ({member.UserId}).");
            unknownRoleRefs += unknown;
            members.Add(member);
        }

        // Never the plugin's flag alone: only a member search that returned exactly the
        // announced number of members proves the list is whole.
        var isComplete = coverage.Complete
            && coverage.Method == DiscordSyncMethods.MemberSearch
            && coverage.ExpectedCount == r.Members.Count;

        return new NormalizedSync(
            GuildId: guild.Id,
            GuildName: guild.Name,
            IconHash: guild.Icon,
            MemberCount: guild.MemberCount,
            Method: coverage.Method,
            DeclaredComplete: coverage.Complete,
            IsComplete: isComplete,
            ExpectedCount: coverage.ExpectedCount,
            CollectedCount: r.Members.Count,
            PluginVersion: r.PluginVersion,
            DeclaredCollectedAt: r.CollectedAt.UtcDateTime,
            CollectionDuration: TimeSpan.FromMilliseconds(r.CollectionDurationMs),
            Roles: roles,
            Members: members,
            UnknownRoleRefCount: unknownRoleRefs);
    }

    private static List<NormalizedRole> NormalizeRoles(IReadOnlyList<DiscordSyncRole> source)
    {
        var roles = new List<NormalizedRole>(source.Count);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < source.Count; i++)
        {
            var role = source[i];
            if (role is null) Fail($"roles[{i}] est vide.");
            if (!DiscordSnowflake.IsValid(role.Id)) Fail($"roles[{i}].id : snowflake attendu.");
            if (!ids.Add(role.Id)) Fail($"roles[{i}].id : rôle en double ({role.Id}).");
            if (string.IsNullOrEmpty(role.Name)) Fail($"roles[{i}].name est obligatoire.");
            if (TooLong(role.Name, MaxRoleNameLength)) Fail($"roles[{i}].name : {MaxRoleNameLength} caractères au plus.");
            if (role.Position is < 0 or > MaxRolePosition) Fail($"roles[{i}].position : entre 0 et {MaxRolePosition}.");
            if (role.Color is not null && !HexColor.IsMatch(role.Color)) Fail($"roles[{i}].color : #rrggbb attendu.");
            roles.Add(new NormalizedRole(role.Id, role.Name, role.Position, role.Color?.ToLowerInvariant(), role.Hoist, role.Managed));
        }
        return roles;
    }

    private static NormalizedMember NormalizeMember(
        DiscordSyncMember? m, int i, IReadOnlySet<string> knownRoleIds, out int unknownRoleRefs)
    {
        if (m is null) Fail($"members[{i}] est vide.");
        if (!DiscordSnowflake.IsValid(m.UserId)) Fail($"members[{i}].userId : snowflake attendu.");
        if (string.IsNullOrEmpty(m.Username)) Fail($"members[{i}].username est obligatoire.");
        if (TooLong(m.Username, MaxNameLength)) Fail($"members[{i}].username : {MaxNameLength} caractères au plus.");
        var globalName = Clean(m.GlobalName);
        if (globalName is not null && TooLong(globalName, MaxNameLength))
            Fail($"members[{i}].globalName : {MaxNameLength} caractères au plus.");
        var nick = Clean(m.Nick);
        if (nick is not null && TooLong(nick, MaxNameLength))
            Fail($"members[{i}].nick : {MaxNameLength} caractères au plus.");
        if (m.RoleIds is null) Fail($"members[{i}].roleIds est obligatoire.");
        if (m.RoleIds.Count > MaxRoleIdsPerMember) Fail($"members[{i}].roleIds : {MaxRoleIdsPerMember} au plus.");

        var distinct = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var roleId in m.RoleIds)
        {
            if (!DiscordSnowflake.IsValid(roleId)) Fail($"members[{i}].roleIds : snowflake attendu.");
            distinct.Add(roleId);
        }

        // A role missing from the payload was deleted during the collection: not an error.
        var roleIds = new List<string>(distinct.Count);
        unknownRoleRefs = 0;
        foreach (var roleId in distinct)
        {
            if (knownRoleIds.Contains(roleId)) roleIds.Add(roleId);
            else unknownRoleRefs++;
        }

        return new NormalizedMember(
            m.UserId, m.Username, globalName, nick, roleIds,
            m.JoinedAt is { } joinedAt ? TruncateToSecond(joinedAt.UtcDateTime) : null,
            m.Bot);
    }

    private static void RequireText(string? value, int max, string field)
    {
        if (string.IsNullOrEmpty(value)) Fail($"{field} est obligatoire.");
        if (TooLong(value, max)) Fail($"{field} : {max} caractères au plus.");
    }

    private static void RequireCount(int? value, string field)
    {
        if (value is < 0 or > MaxCount) Fail($"{field} : entre 0 et {MaxCount}.");
    }

    /// <summary>Length in Unicode scalar values, the unit Discord counts in (an emoji is one).</summary>
    private static bool TooLong(string value, int max)
    {
        if (value.Length <= max) return false;   // never more scalar values than UTF-16 units
        var count = 0;
        foreach (var _ in value.EnumerateRunes())
        {
            if (++count > max) return true;
        }
        return false;
    }

    /// <summary>Trims a display name; a blank one reads as absent.</summary>
    private static string? Clean(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static DateTime TruncateToSecond(DateTime utc) =>
        new(utc.Ticks - utc.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);

    [DoesNotReturn]
    private static void Fail(string detail) => throw new ValidationException(detail, DiscordErrorCodes.InvalidSync);
}
