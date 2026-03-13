using Collector.Data.Repositories;
using Collector.Models;
using Microsoft.Extensions.Logging;

namespace Collector.Services;

// ── Result types ─────────────────────────────────────────────────────────────

public class FieldDiscrepancy
{
    public string Field { get; init; } = null!;
    public string? DbValue { get; init; }
    public string? LiveValue { get; init; }
}

public class OrgIntegrityResult
{
    public string Sid { get; init; } = null!;
    public string DbName { get; init; } = null!;
    public bool NotFoundLive { get; init; }
    public DateTime DbTimestamp { get; init; }
    public List<FieldDiscrepancy> Discrepancies { get; init; } = [];
    public bool IsClean => !NotFoundLive && Discrepancies.Count == 0;
}

public class IntegrityCheckReport
{
    public DateTime CheckedAt { get; init; }
    public int SampleSize { get; init; }
    public List<OrgIntegrityResult> Results { get; init; } = [];
    public int NotFound => Results.Count(r => r.NotFoundLive);
    public int Clean => Results.Count(r => r.IsClean);
    public int WithDiscrepancies => Results.Count(r => !r.NotFoundLive && !r.IsClean);
    public int TotalDiscrepancies => Results.Sum(r => r.Discrepancies.Count);
}

// ── Interface ─────────────────────────────────────────────────────────────────

public interface IIntegrityCheckService
{
    /// <summary>
    /// Picks <paramref name="sampleSize"/> random organizations from DB,
    /// fetches their live data from RSI and reports field discrepancies.
    /// </summary>
    Task<IntegrityCheckReport> RunCheckAsync(int sampleSize = 10, CancellationToken ct = default);
}

// ── Implementation ────────────────────────────────────────────────────────────

public class IntegrityCheckService : IIntegrityCheckService
{
    private readonly IOrganizationRepository _orgRepo;
    private readonly IRsiApiClient _apiClient;
    private readonly ILogger<IntegrityCheckService> _logger;

    public IntegrityCheckService(
        IOrganizationRepository orgRepo,
        IRsiApiClient apiClient,
        ILogger<IntegrityCheckService> logger)
    {
        _orgRepo = orgRepo;
        _apiClient = apiClient;
        _logger = logger;
    }

    public async Task<IntegrityCheckReport> RunCheckAsync(int sampleSize = 10, CancellationToken ct = default)
    {
        _logger.LogInformation("Integrity check starting — sampling {N} organizations", sampleSize);

        // Pull all latest snapshots and pick a random sample
        var allOrgs = await _orgRepo.GetAllLatestAsync(ct);

        // Only check orgs that have metadata (archetype populated)
        var candidates = allOrgs.Where(o => o.Archetype != null).ToList();

        var sample = candidates
            .OrderBy(_ => Random.Shared.Next())
            .Take(sampleSize)
            .ToList();

        _logger.LogInformation(
            "Candidates with metadata: {Candidates}/{Total}. Checking {Sample} orgs",
            candidates.Count, allOrgs.Count, sample.Count);

        var results = new List<OrgIntegrityResult>();

        for (int i = 0; i < sample.Count; i++)
        {
            var dbOrg = sample[i];
            _logger.LogInformation("[{I}/{N}] Checking {Sid} ({Name})", i + 1, sample.Count, dbOrg.Sid, dbOrg.Name);

            try
            {
                var liveOrg = await _apiClient.GetOrganizationAsync(dbOrg.Sid, ct);

                if (liveOrg == null)
                {
                    _logger.LogWarning("  → Not found live");
                    results.Add(new OrgIntegrityResult
                    {
                        Sid = dbOrg.Sid,
                        DbName = dbOrg.Name,
                        NotFoundLive = true,
                        DbTimestamp = dbOrg.Timestamp
                    });
                    continue;
                }

                var discrepancies = new List<FieldDiscrepancy>();

                Compare(discrepancies, "Name",        dbOrg.Name,                    liveOrg.Name);
                Compare(discrepancies, "MembersCount", dbOrg.MembersCount.ToString(), liveOrg.MembersCount.ToString());
                Compare(discrepancies, "Archetype",   dbOrg.Archetype,               liveOrg.Archetype);
                Compare(discrepancies, "Lang",        dbOrg.Lang,                    liveOrg.Lang);
                Compare(discrepancies, "Commitment",  dbOrg.Commitment,              liveOrg.Commitment);
                Compare(discrepancies, "Recruiting",  dbOrg.Recruiting?.ToString(),  liveOrg.Recruiting?.ToString());
                Compare(discrepancies, "Roleplay",    dbOrg.Roleplay?.ToString(),    liveOrg.Roleplay?.ToString());

                if (discrepancies.Count == 0)
                    _logger.LogInformation("  → OK");
                else
                    foreach (var d in discrepancies)
                        _logger.LogWarning("  → {Field}: DB={Db} | Live={Live}", d.Field, d.DbValue ?? "null", d.LiveValue ?? "null");

                results.Add(new OrgIntegrityResult
                {
                    Sid = dbOrg.Sid,
                    DbName = dbOrg.Name,
                    DbTimestamp = dbOrg.Timestamp,
                    Discrepancies = discrepancies
                });
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "  → Error checking {Sid}", dbOrg.Sid);
            }
        }

        var report = new IntegrityCheckReport
        {
            CheckedAt = DateTime.UtcNow,
            SampleSize = sample.Count,
            Results = results
        };

        LogReport(report);
        return report;
    }

    private static void Compare(List<FieldDiscrepancy> list, string field, string? db, string? live)
    {
        // Normalize: null == "" == "False" for bool-like fields comparison
        var dbNorm  = Normalize(db);
        var liveNorm = Normalize(live);

        if (!string.Equals(dbNorm, liveNorm, StringComparison.OrdinalIgnoreCase))
            list.Add(new FieldDiscrepancy { Field = field, DbValue = db, LiveValue = live });
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        // Normalize booleans
        if (value.Equals("True", StringComparison.OrdinalIgnoreCase))  return "true";
        if (value.Equals("False", StringComparison.OrdinalIgnoreCase)) return "false";
        return value.Trim();
    }

    private void LogReport(IntegrityCheckReport report)
    {
        _logger.LogInformation("─── Integrity Check Report ───────────────────");
        _logger.LogInformation("Checked : {N} organizations", report.SampleSize);
        _logger.LogInformation("Clean   : {C}", report.Clean);
        _logger.LogInformation("Errors  : {E} orgs, {D} field discrepancies", report.WithDiscrepancies, report.TotalDiscrepancies);
        _logger.LogInformation("Missing : {M} (not found live)", report.NotFound);

        if (report.TotalDiscrepancies > 0)
        {
            _logger.LogInformation("─── Discrepancies ────────────────────────────");
            foreach (var r in report.Results.Where(r => r.Discrepancies.Count > 0))
            {
                _logger.LogWarning("{Sid} ({Name}) — snapshot: {Ts:yyyy-MM-dd HH:mm}", r.Sid, r.DbName, r.DbTimestamp);
                foreach (var d in r.Discrepancies)
                    _logger.LogWarning("  {Field}: DB=\"{Db}\" → Live=\"{Live}\"", d.Field, d.DbValue ?? "null", d.LiveValue ?? "null");
            }
        }

        _logger.LogInformation("──────────────────────────────────────────────");
    }
}
