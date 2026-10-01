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
        var currentPeople = await CurrentByCitizenIdAsync(
            roster.Concat(former).Where(r => r.CitizenId is not null)
                .Select(r => r.CitizenId!.Value).Distinct().ToList(), ct);
        foreach (var row in roster)
        {
            // Roster rows may predate the citizen id: users fills it in when it knows the handle.
            userByHandle.TryGetValue(row.Handle, out var profile);
            var citizenId = row.CitizenId ?? profile?.CitizenId;
            var person = citizenId is int cid ? currentPeople.GetValueOrDefault(cid) : null;
            matches.Add(new HandleMatch(row.Handle, person?.Handle ?? row.Handle, citizenId,
                person?.DisplayName ?? row.DisplayName ?? profile?.DisplayName, Strong: true));
        }
        foreach (var row in userByHandle.Values)
            matches.Add(new HandleMatch(row.Handle, row.Handle, row.CitizenId, row.DisplayName, Strong: false));

        if (former.Count > 0)
        {
            foreach (var row in former
                         .OrderByDescending(f => f.SeenAt)
                         .DistinctBy(f => (f.CitizenId, f.Handle.ToLowerInvariant())))
            {
                if (currentPeople.TryGetValue(row.CitizenId!.Value, out var person))
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
            .Where(m => EF.Functions.Collate(m.UserHandle, "NOCASE") == handle)
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
        if (citizenIds.Count == 0) return [];
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
