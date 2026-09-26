using Collector.Data;
using Collector.Parsers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Collector.Services;

/// <summary>
/// A stored roster against the one RSI serves now: members only one side has, and
/// rank changes. <see cref="Skipped"/> says why a roster was not compared.
/// </summary>
public sealed record RosterComparison(
    string Sid,
    DateTime ReadAt,
    int StoredTotal,
    int LiveTotal,
    IReadOnlyList<string> OnlyStored,
    IReadOnlyList<string> OnlyLive,
    IReadOnlyList<string> RankDiffers,
    string? Skipped)
{
    public bool IsSame => Skipped == null && StoredTotal == LiveTotal
        && OnlyStored.Count == 0 && OnlyLive.Count == 0 && RankDiffers.Count == 0;
}

public sealed record ProfileComparison(string Handle, DateTime ReadAt, IReadOnlyList<FieldDiscrepancy> Differences, string? Skipped);

/// <summary>
/// Spot check against the source (<c>Collector --integrity-check</c>): a random sample
/// of rosters and citizen profiles the collector read recently, read again from RSI
/// and compared field by field. Differences are either changes on RSI since the read
/// (the report gives its age) or collection faults; a clean sample means what is
/// stored is what RSI shows. Only complete, small rosters are sampled, to keep the
/// number of requests low.
/// </summary>
public sealed class SourceComparisonService
{
    private readonly TrackerDbContext _db;
    private readonly IRsiApiClient _rsi;
    private readonly UserProfileHtmlParser _parser;
    private readonly ILogger<SourceComparisonService> _logger;

    public SourceComparisonService(
        TrackerDbContext db, IRsiApiClient rsi, UserProfileHtmlParser parser, ILogger<SourceComparisonService> logger)
    {
        _db = db;
        _rsi = rsi;
        _parser = parser;
        _logger = logger;
    }

    public async Task<IReadOnlyList<RosterComparison>> CompareRostersAsync(
        int sampleSize, DateTime since, int maxRows = 320, CancellationToken ct = default)
    {
        // Organizations whose last read was complete (visible split known) and small.
        var sample = await SampleAsync(
            """
            SELECT Sid, LastMembersCollectedAt FROM (
                SELECT d.Sid, d.LastMembersCollectedAt,
                    (SELECT c.VisibleCount FROM org_member_counts c WHERE c.OrgSid = d.Sid ORDER BY c.CollectedAt DESC LIMIT 1) AS Visible,
                    (SELECT c.TotalRows FROM org_member_counts c WHERE c.OrgSid = d.Sid ORDER BY c.CollectedAt DESC LIMIT 1) AS TotalRows
                FROM discovered_organizations d
                WHERE d.LastMembersCollectedAt >= $since AND d.DeadAt IS NULL)
            WHERE Visible > 0 AND TotalRows <= $maxRows
            ORDER BY random() LIMIT $sample
            """, since, sampleSize, maxRows, ct);

        var results = new List<RosterComparison>();
        foreach (var (sid, readAt) in sample)
        {
            var stored = await _db.OrganizationMembers.AsNoTracking()
                .Where(m => m.OrgSid == sid && m.IsActive)
                .Select(m => new { m.UserHandle, m.Rank })
                .ToListAsync(ct);
            var storedTotal = await _db.OrgMemberCounts.AsNoTracking()
                .Where(c => c.OrgSid == sid).OrderByDescending(c => c.CollectedAt).Select(c => c.TotalRows).FirstAsync(ct);

            var live = await _rsi.GetAllOrganizationMembersAsync(sid, ct: ct);
            if (live.Status != RosterStatus.Complete)
            {
                results.Add(new RosterComparison(sid, readAt, storedTotal, live.TotalRows, [], [], [], $"live read {live.Status}"));
                continue;
            }

            var storedByHandle = stored.ToDictionary(m => m.UserHandle, m => m.Rank, StringComparer.OrdinalIgnoreCase);
            var liveByHandle = live.Members.ToDictionary(m => m.Handle, m => m.Rank, StringComparer.OrdinalIgnoreCase);
            var comparison = new RosterComparison(
                sid, readAt, storedTotal, live.TotalRows,
                storedByHandle.Keys.Where(h => !liveByHandle.ContainsKey(h)).Order().ToList(),
                liveByHandle.Keys.Where(h => !storedByHandle.ContainsKey(h)).Order().ToList(),
                storedByHandle
                    .Where(s => liveByHandle.TryGetValue(s.Key, out var rank) && rank != s.Value)
                    .Select(s => $"{s.Key}: {s.Value} → {liveByHandle[s.Key]}")
                    .Order().ToList(),
                null);
            results.Add(comparison);
            Log(comparison);
        }

        var compared = results.Where(r => r.Skipped == null).ToList();
        _logger.LogInformation(
            "Rosters: {Compared} compared, {Same} identical, {Different} different ({OnlyStored} members only stored, {OnlyLive} only on RSI, {Ranks} rank changes), {Skipped} skipped",
            compared.Count, compared.Count(r => r.IsSame), compared.Count(r => !r.IsSame),
            compared.Sum(r => r.OnlyStored.Count), compared.Sum(r => r.OnlyLive.Count), compared.Sum(r => r.RankDiffers.Count),
            results.Count - compared.Count);
        return results;
    }

    public async Task<IReadOnlyList<ProfileComparison>> CompareProfilesAsync(
        int sampleSize, DateTime since, CancellationToken ct = default)
    {
        var sample = await SampleAsync(
            "SELECT UserHandle, UpdatedAt FROM users WHERE UpdatedAt >= $since ORDER BY random() LIMIT $sample",
            since, sampleSize, 0, ct);

        var results = new List<ProfileComparison>();
        foreach (var (handle, readAt) in sample)
        {
            var stored = await _db.Users.AsNoTracking().FirstAsync(u => u.UserHandle == handle, ct);
            var fetched = await _rsi.GetUserProfileResultAsync(handle, ct);
            if (fetched.Outcome != UserProfileFetchOutcome.Ok || fetched.Html == null)
            {
                results.Add(new ProfileComparison(handle, readAt, [], $"live profile {fetched.Outcome}"));
                continue;
            }
            var live = _parser.ParseProfile(fetched.Html);
            if (live.Data == null)
            {
                results.Add(new ProfileComparison(handle, readAt, [], $"live profile {live.Outcome}"));
                continue;
            }

            var differences = new List<FieldDiscrepancy>();
            void Compare(string field, string? db, string? rsi)
            {
                if (!string.Equals(db ?? "", rsi ?? "", StringComparison.Ordinal))
                    differences.Add(new FieldDiscrepancy { Field = field, DbValue = db, LiveValue = rsi });
            }
            Compare("CitizenId", stored.CitizenId.ToString(), live.Data.CitizenId.ToString());
            Compare("Handle", stored.UserHandle, live.Data.Handle);
            Compare("DisplayName", stored.DisplayName, live.Data.DisplayName);
            Compare("Location", stored.Location, live.Data.Location);
            Compare("Enlisted", stored.Enlisted?.ToString("yyyy-MM-dd"), live.Data.Enlisted?.ToString("yyyy-MM-dd"));
            Compare("Bio", stored.Bio, live.Data.Bio);
            Compare("UrlImage", stored.UrlImage, live.Data.UrlImage);

            var comparison = new ProfileComparison(handle, readAt, differences, null);
            results.Add(comparison);
            if (differences.Count > 0)
            {
                _logger.LogWarning("Profile {Handle} (read {Age:F0} h ago): {Differences}", handle,
                    (DateTime.UtcNow - readAt).TotalHours,
                    string.Join("; ", differences.Select(d => $"{d.Field}: \"{d.DbValue}\" → \"{d.LiveValue}\"")));
            }
        }

        var compared = results.Where(r => r.Skipped == null).ToList();
        _logger.LogInformation(
            "Profiles: {Compared} compared, {Same} identical, {Different} different, {Skipped} skipped ({Reasons})",
            compared.Count, compared.Count(r => r.Differences.Count == 0), compared.Count(r => r.Differences.Count > 0),
            results.Count - compared.Count,
            string.Join(", ", results.Where(r => r.Skipped != null).GroupBy(r => r.Skipped).Select(g => $"{g.Key}: {g.Count()}")));
        return results;
    }

    private void Log(RosterComparison r)
    {
        var age = (DateTime.UtcNow - r.ReadAt).TotalHours;
        if (r.IsSame)
        {
            _logger.LogInformation("Roster {Sid} (read {Age:F0} h ago): identical, {Total} rows", r.Sid, age, r.LiveTotal);
            return;
        }
        _logger.LogWarning(
            "Roster {Sid} (read {Age:F0} h ago): total {Stored} → {Live}; only stored: {OnlyStored}; only on RSI: {OnlyLive}; ranks: {Ranks}",
            r.Sid, age, r.StoredTotal, r.LiveTotal,
            string.Join(", ", r.OnlyStored), string.Join(", ", r.OnlyLive), string.Join(", ", r.RankDiffers));
    }

    private async Task<List<(string Key, DateTime ReadAt)>> SampleAsync(
        string sql, DateTime since, int sampleSize, int maxRows, CancellationToken ct)
    {
        await _db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var command = ((SqliteConnection)_db.Database.GetDbConnection()).CreateCommand();
            command.CommandText = sql;
            command.CommandTimeout = 0;
            command.Parameters.AddWithValue("$since", DateTime.SpecifyKind(since, DateTimeKind.Utc));
            command.Parameters.AddWithValue("$sample", sampleSize);
            command.Parameters.AddWithValue("$maxRows", maxRows);
            var rows = new List<(string, DateTime)>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                rows.Add((reader.GetString(0), DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc)));
            }
            return rows;
        }
        finally
        {
            await _db.Database.CloseConnectionAsync();
        }
    }
}
