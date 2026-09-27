using Collector.Data;
using Collector.Parsers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Collector.Services;

/// <summary>
/// One consistency check: the rows breaking it, a few of them as examples.
/// <see cref="Informational"/> checks count cases to review, not errors.
/// </summary>
public sealed record VerificationResult(
    string Name, string Description, bool Informational, long Count, IReadOnlyList<string> Samples)
{
    public bool Failed => !Informational && Count > 0;
}

/// <summary>
/// Read-only consistency checks of what the collector wrote since a date
/// (<c>Collector --maintenance verify [--since yyyy-MM-ddTHH:mm]</c>): rosters against
/// their last read, counters against rosters, events against rosters, citizens and
/// the enrichment queue. Scoped by date so that rows written by older versions do
/// not hide what the current one does. Safe to run while the collector runs.
/// </summary>
public sealed class DataVerificationService
{
    /// <summary><paramref name="Keep"/> narrows the rows the SQL returns where SQL cannot say it.</summary>
    private sealed record Check(
        string Name, string Description, string Sql, bool Informational = false, Func<SqliteDataReader, bool>? Keep = null);

    /// <summary>Organizations whose roster Phase 3 read since the date.</summary>
    private const string OrgsReadSince =
        "SELECT Sid FROM discovered_organizations WHERE LastMembersCollectedAt >= $since";

    /// <summary>Each such organization's latest roster log, when written since the date.</summary>
    private const string LatestLogs = $"""
        latest AS (
            -- One index seek per organization (a GROUP BY would walk every log entry).
            SELECT OrgSid, t FROM (
                SELECT d.Sid AS OrgSid,
                    (SELECT MAX(l.CollectionTime) FROM member_collection_log l WHERE l.OrgSid = d.Sid) AS t
                FROM discovered_organizations d WHERE d.LastMembersCollectedAt >= $since)
            WHERE t >= $since),
        logged AS (
            SELECT l.OrgSid, l.UserHandle FROM member_collection_log l
            JOIN latest ON latest.OrgSid = l.OrgSid AND l.CollectionTime = latest.t),
        active AS (
            SELECT m.OrgSid, m.UserHandle FROM organization_members m
            WHERE m.IsActive = 1 AND m.OrgSid IN (SELECT OrgSid FROM latest))
        """;

    /// <summary>Latest RSI count and MembersCount of each organization read since the date.</summary>
    private const string OrgCounts = $"""
        SELECT d.Sid,
            (SELECT o.MembersCount FROM organizations o WHERE o.Sid = d.Sid ORDER BY o.Timestamp DESC LIMIT 1) AS MembersCount,
            (SELECT c.TotalRows FROM org_member_counts c WHERE c.OrgSid = d.Sid ORDER BY c.CollectedAt DESC LIMIT 1) AS TotalRows,
            (SELECT c.VisibleCount FROM org_member_counts c WHERE c.OrgSid = d.Sid ORDER BY c.CollectedAt DESC LIMIT 1) AS Visible,
            (SELECT c.RedactedCount FROM org_member_counts c WHERE c.OrgSid = d.Sid ORDER BY c.CollectedAt DESC LIMIT 1) AS Redacted,
            (SELECT c.HiddenCount FROM org_member_counts c WHERE c.OrgSid = d.Sid ORDER BY c.CollectedAt DESC LIMIT 1) AS Hidden,
            (SELECT COUNT(*) FROM organization_members m WHERE m.OrgSid = d.Sid AND m.IsActive = 1) AS Active
        FROM discovered_organizations d WHERE d.LastMembersCollectedAt >= $since
        """;

    private const string ListingFields = "Name, Archetype, Lang, Commitment, Recruiting, Roleplay, UrlImage, UrlCorpo";

    private const string ContentFields =
        "Description, History, Manifesto, Charter, FocusPrimaryName, FocusPrimaryImage, FocusSecondaryName, FocusSecondaryImage";

    private static readonly Check[] Checks =
    [
        // ── Rosters (Phase 3) ──
        new("duplicate-active-members",
            "a member active twice in the same organization (handles compared regardless of case)",
            $"""
            SELECT OrgSid, UserHandle, COUNT(*) FROM organization_members
            WHERE IsActive = 1 AND OrgSid IN ({OrgsReadSince})
            GROUP BY OrgSid, UserHandle COLLATE NOCASE HAVING COUNT(*) > 1
            """),
        new("read-but-not-active",
            "a member of the organization's last roster read who is not active (organizations emptied by ErrInvalidOrganization excluded)",
            $"""
            WITH {LatestLogs}
            SELECT OrgSid, UserHandle FROM (SELECT OrgSid, UserHandle FROM logged EXCEPT SELECT OrgSid, UserHandle FROM active)
            WHERE OrgSid IN (SELECT OrgSid FROM active)
            """),
        new("active-but-not-read",
            "an active member missing from the organization's last roster read",
            $"""
            WITH {LatestLogs}
            SELECT OrgSid, UserHandle FROM active EXCEPT SELECT OrgSid, UserHandle FROM logged
            """),
        new("emptied-rosters",
            "organizations whose last read was written but who have no active member: only ErrInvalidOrganization should do this",
            $"""
            WITH {LatestLogs}
            SELECT OrgSid FROM latest WHERE OrgSid NOT IN (SELECT OrgSid FROM active)
            """,
            Informational: true),
        new("members-count-differs-from-rsi",
            "MembersCount of the latest snapshot differs from RSI's latest totalrows",
            $"SELECT Sid, MembersCount, TotalRows FROM ({OrgCounts}) WHERE TotalRows > 0 AND MembersCount > 0 AND MembersCount <> TotalRows"),
        new("active-count-differs-from-visible",
            "after a complete read, the number of active members differs from the visible rows RSI served",
            $"SELECT Sid, Visible, Active FROM ({OrgCounts}) WHERE Visible > 0 AND Visible <> Active"),
        new("counters-do-not-add-up",
            "visible + redacted + hidden differs from RSI's totalrows on a complete read (RSI's own counts, or rows read twice)",
            $"SELECT Sid, TotalRows, Visible, Redacted, Hidden FROM ({OrgCounts}) WHERE Visible IS NOT NULL AND Visible + Redacted + Hidden <> TotalRows",
            Informational: true),
        // Orgs name their ranks as they like, "Affiliate" included: only a whole read
        // made of overlay titles is the v1 parser's fault.
        new("overlay-titles-as-ranks",
            "a v2 roster read of 5+ members in which every rank is an overlay title (Roles, Affiliate)",
            $"""
            SELECT OrgSid, CollectionTime, COUNT(*) FROM member_collection_log
            WHERE OrgSid IN ({OrgsReadSince}) AND CollectionTime >= $since AND ParserVersion = 2
            GROUP BY OrgSid, CollectionTime
            HAVING COUNT(*) >= 5 AND SUM(Rank IN ('Roles', 'Affiliate')) = COUNT(*)
            """),
        new("stars-out-of-range",
            "an active member with a star count outside 0-5",
            $"""
            SELECT OrgSid, UserHandle, Stars FROM organization_members
            WHERE IsActive = 1 AND OrgSid IN ({OrgsReadSince}) AND Stars NOT BETWEEN 0 AND 5
            """),

        // ── Events ──
        new("left-but-still-active",
            "a member_left whose member is still active from that read or an earlier one",
            """
            SELECT e.Id, e.OrgSid, e.UserHandle, e.Timestamp FROM change_events e
            WHERE e.ChangeType = 'member_left' AND e.Timestamp >= $since
              AND EXISTS (SELECT 1 FROM organization_members m
                          WHERE m.OrgSid = e.OrgSid AND m.UserHandle = e.UserHandle AND m.IsActive = 1 AND m.Timestamp <= e.Timestamp)
            """),
        new("joined-but-never-in-roster",
            "a member_joined for someone who never appears in that organization's roster",
            """
            SELECT e.Id, e.OrgSid, e.UserHandle FROM change_events e
            WHERE e.ChangeType = 'member_joined' AND e.Timestamp >= $since
              AND NOT EXISTS (SELECT 1 FROM organization_members m WHERE m.OrgSid = e.OrgSid AND m.UserHandle = e.UserHandle)
            """),
        new("duplicate-events",
            "the same change recorded twice in the same second",
            """
            SELECT ChangeType, EntityId, OrgSid, UserHandle, COUNT(*) FROM change_events
            WHERE Timestamp >= $since
            GROUP BY ChangeType, EntityType, EntityId, OrgSid, UserHandle, OldValue, NewValue, substr(Timestamp, 1, 19)
            HAVING COUNT(*) > 1
            """),
        new("citizen-field-first-value-as-change",
            "a display_name_changed or location_changed from an unknown value: a first value is not a change",
            """
            SELECT Id, ChangeType, EntityId, NewValue FROM change_events
            WHERE ChangeType IN ('display_name_changed', 'location_changed') AND Timestamp >= $since
              AND (OldValue IS NULL OR OldValue = '')
            """),
        new("page-text-from-empty",
            "an organization text changed from empty: an org that added a text, or a first read counted as a change",
            """
            SELECT Id, ChangeType, OrgSid FROM change_events
            WHERE ChangeType IN ('description_changed', 'history_changed', 'manifesto_changed', 'charter_changed',
                                 'focus_primary_changed', 'focus_secondary_changed')
              AND Timestamp >= $since AND (OldValue IS NULL OR OldValue = '')
            """,
            Informational: true),
        new("rename-to-same-handle",
            "a handle_changed whose old and new handles are the same",
            """
            SELECT Id, OldValue, NewValue FROM change_events
            WHERE ChangeType = 'handle_changed' AND Timestamp >= $since AND OldValue = NewValue
            """),

        // ── Organization snapshots (Phases 1-2) ──
        new("unchanged-listing-snapshot",
            "a listing snapshot identical to the organization's previous snapshot",
            $"""
            SELECT Sid, Timestamp FROM (
                SELECT Sid, Timestamp, ContentCollected, {ListingFields},
                    LAG(Timestamp) OVER w AS pTimestamp,
                    LAG(Name) OVER w AS pName, LAG(Archetype) OVER w AS pArchetype, LAG(Lang) OVER w AS pLang,
                    LAG(Commitment) OVER w AS pCommitment, LAG(Recruiting) OVER w AS pRecruiting,
                    LAG(Roleplay) OVER w AS pRoleplay, LAG(UrlImage) OVER w AS pUrlImage, LAG(UrlCorpo) OVER w AS pUrlCorpo
                FROM organizations
                WHERE Sid IN (SELECT Sid FROM organizations WHERE Timestamp >= $since AND ContentCollected = 0)
                WINDOW w AS (PARTITION BY Sid ORDER BY Timestamp))
            WHERE ContentCollected = 0 AND Timestamp >= $since AND pTimestamp IS NOT NULL
              AND Name IS pName AND Archetype IS pArchetype AND Lang IS pLang AND Commitment IS pCommitment
              AND Recruiting IS pRecruiting AND Roleplay IS pRoleplay AND UrlImage IS pUrlImage AND UrlCorpo IS pUrlCorpo
            """),
        new("unchanged-content-snapshot",
            "a page-content snapshot identical to the organization's previous content snapshot",
            $"""
            SELECT Sid, Timestamp FROM (
                SELECT Sid, Timestamp, {ContentFields},
                    LAG(Timestamp) OVER w AS pTimestamp,
                    LAG(Description) OVER w AS pDescription, LAG(History) OVER w AS pHistory,
                    LAG(Manifesto) OVER w AS pManifesto, LAG(Charter) OVER w AS pCharter,
                    LAG(FocusPrimaryName) OVER w AS pFocusPrimaryName, LAG(FocusPrimaryImage) OVER w AS pFocusPrimaryImage,
                    LAG(FocusSecondaryName) OVER w AS pFocusSecondaryName, LAG(FocusSecondaryImage) OVER w AS pFocusSecondaryImage
                FROM organizations
                WHERE ContentCollected = 1
                  AND Sid IN (SELECT Sid FROM organizations WHERE Timestamp >= $since AND ContentCollected = 1)
                WINDOW w AS (PARTITION BY Sid ORDER BY Timestamp))
            WHERE Timestamp >= $since AND pTimestamp IS NOT NULL
              AND COALESCE(Description, '') = COALESCE(pDescription, '') AND COALESCE(History, '') = COALESCE(pHistory, '')
              AND COALESCE(Manifesto, '') = COALESCE(pManifesto, '') AND COALESCE(Charter, '') = COALESCE(pCharter, '')
              AND COALESCE(FocusPrimaryName, '') = COALESCE(pFocusPrimaryName, '')
              AND COALESCE(FocusPrimaryImage, '') = COALESCE(pFocusPrimaryImage, '')
              AND COALESCE(FocusSecondaryName, '') = COALESCE(pFocusSecondaryName, '')
              AND COALESCE(FocusSecondaryImage, '') = COALESCE(pFocusSecondaryImage, '')
            """),
        new("org-name-html-entities",
            "an organization snapshot whose name is still HTML-encoded (\"Steal &amp; Deal\")",
            "SELECT Sid, Name FROM organizations WHERE Timestamp >= $since AND Name LIKE '%&%;%'",
            // "Salt & Pepper; Co" matches the LIKE but decodes to itself.
            Keep: row => HtmlText.Decode(row.GetString(1)) != row.GetString(1)),
        new("dead-org-read",
            "a roster read after the organization was declared dead",
            """
            SELECT Sid, DeadAt, LastMembersCollectedAt FROM discovered_organizations
            WHERE DeadAt IS NOT NULL AND LastMembersCollectedAt >= $since AND LastMembersCollectedAt > DeadAt
            """),

        // ── Citizens (Phase 4) ──
        // A reused handle is held by two citizens until the former owner is read again:
        // the roster row must match one of them.
        new("roster-and-citizen-disagree",
            "an active roster row whose citizen is known under neither that handle nor a former one (case ignored)",
            $"""
            SELECT m.OrgSid, m.UserHandle, m.CitizenId FROM organization_members m
            WHERE m.OrgSid IN ({OrgsReadSince}) AND m.IsActive = 1 AND m.Timestamp >= $since AND m.CitizenId IS NOT NULL
              AND NOT EXISTS (SELECT 1 FROM users u
                              WHERE u.CitizenId = m.CitizenId AND u.UserHandle = m.UserHandle COLLATE NOCASE)
              AND NOT EXISTS (SELECT 1 FROM user_handle_history h
                              WHERE h.CitizenId = m.CitizenId AND h.UserHandle = m.UserHandle COLLATE NOCASE)
            """),
        new("handle-held-by-two-citizens",
            "handles stored for two citizens: a handle given up and taken by someone else, until the former owner is read again",
            "SELECT UserHandle, COUNT(*) FROM users GROUP BY UserHandle COLLATE NOCASE HAVING COUNT(*) > 1",
            Informational: true),
        new("citizen-profile-incomplete",
            "a citizen updated from a profile without a display name or enlistment date (every profile has both)",
            """
            SELECT CitizenId, UserHandle FROM users
            WHERE UpdatedAt >= $since AND (DisplayName IS NULL OR Enlisted IS NULL)
            """),
        new("location-format",
            "a location not written as \"Country, Region\"",
            "SELECT CitizenId, Location FROM users WHERE UpdatedAt >= $since AND Location LIKE '% ,%'"),
        // Whatever the date: what Phase 4 still has to read again.
        new("profiles-to-read-again",
            "a citizen whose profile an older parser stored, not read again yet",
            $"SELECT CitizenId, UserHandle FROM users WHERE ParserVersion < {UserProfileHtmlParser.Version}",
            Informational: true),
        new("queue-state",
            "an enrichment queue row whose Enriched flag contradicts its outcome",
            """
            SELECT Id, UserHandle, Enriched, Outcome FROM user_enrichment_queue
            WHERE (Enriched = 0 AND Outcome IN ('enriched', 'gone', 'abandoned'))
               OR (Enriched = 1 AND EnrichedAt >= $since AND (Outcome IS NULL OR Outcome IN ('na', 'failed')))
            """),
    ];

    public static IReadOnlyList<string> CheckNames { get; } = Checks.Select(c => c.Name).ToList();

    private readonly TrackerDbContext _db;
    private readonly ILogger<DataVerificationService> _logger;

    public DataVerificationService(TrackerDbContext db, ILogger<DataVerificationService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<IReadOnlyList<VerificationResult>> VerifyAsync(DateTime since, CancellationToken ct = default)
    {
        var results = new List<VerificationResult>();
        await _db.Database.OpenConnectionAsync(ct);
        try
        {
            var connection = (SqliteConnection)_db.Database.GetDbConnection();
            foreach (var check in Checks)
            {
                var started = DateTime.UtcNow;
                await using var command = connection.CreateCommand();
                command.CommandText = check.Sql;
                command.CommandTimeout = 0;
                command.Parameters.AddWithValue("$since", DateTime.SpecifyKind(since, DateTimeKind.Utc));

                long count = 0;
                var samples = new List<string>();
                await using (var reader = await command.ExecuteReaderAsync(ct))
                {
                    while (await reader.ReadAsync(ct))
                    {
                        if (check.Keep != null && !check.Keep(reader)) continue;
                        if (count++ < 5)
                        {
                            samples.Add(string.Join(" | ", Enumerable.Range(0, reader.FieldCount)
                                .Select(i => reader.IsDBNull(i) ? "null" : Convert.ToString(reader.GetValue(i)))));
                        }
                    }
                }

                var result = new VerificationResult(check.Name, check.Description, check.Informational, count, samples);
                results.Add(result);
                var level = result.Failed ? LogLevel.Warning : LogLevel.Information;
                _logger.Log(level, "{Status,-5} {Name}: {Count} ({Seconds:F1}s) — {Description}",
                    result.Failed ? "FAIL" : check.Informational ? "info" : "ok", check.Name, count,
                    (DateTime.UtcNow - started).TotalSeconds, check.Description);
                foreach (var sample in samples)
                {
                    _logger.Log(level, "        e.g. {Sample}", sample);
                }
            }
        }
        finally
        {
            await _db.Database.CloseConnectionAsync();
        }
        return results;
    }
}
