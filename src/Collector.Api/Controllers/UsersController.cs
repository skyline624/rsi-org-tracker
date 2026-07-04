using Collector.Api.Dtos.Common;
using Collector.Api.Dtos.Users;
using Collector.Api.Dtos.Changes;
using Collector.Api.Dtos.Organizations;
using Collector.Api.Extensions;
using Collector.Data;
using Collector.Data.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Collector.Api.Controllers;

[ApiController]
[Route("api/users")]
[Authorize]
public class UsersController : ControllerBase
{
    private readonly IUserRepository _userRepo;
    private readonly IUserHandleHistoryRepository _handleHistoryRepo;
    private readonly IOrganizationMemberRepository _memberRepo;
    private readonly IChangeEventRepository _changeRepo;
    private readonly TrackerDbContext _db;

    public UsersController(
        IUserRepository userRepo,
        IUserHandleHistoryRepository handleHistoryRepo,
        IOrganizationMemberRepository memberRepo,
        IChangeEventRepository changeRepo,
        TrackerDbContext db)
    {
        _userRepo = userRepo;
        _handleHistoryRepo = handleHistoryRepo;
        _memberRepo = memberRepo;
        _changeRepo = changeRepo;
        _db = db;
    }

    [HttpGet]
    public async Task<ActionResult<PaginatedResponse<UserProfileDto>>> GetAll(
        [FromQuery] string? search,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        // No search term: list enriched citizens only. A global listing of the ~600k
        // non-enriched roster handles would be huge and meaningless, so we keep the
        // default directory to real profiles (fast: single index-free scan of `users`).
        if (string.IsNullOrWhiteSpace(search))
        {
            var all = _db.Users.AsNoTracking().OrderBy(u => u.UserHandle);
            return Ok(await all.ToPaginatedAsync(page, pageSize, u => u.ToProfileDto(), ct));
        }

        return Ok(await SearchUsersAsync(search, page, pageSize, ct));
    }

    /// <summary>
    /// Searches enriched citizens AND roster-only members (handles tracked in
    /// organization_members that never got a CitizenId, so they can't exist in
    /// `users` — e.g. RSI accounts that hide their citizen number). EF Core cannot
    /// translate a UNION of these two differently-shaped projections under SQLite, so
    /// the combined set is expressed as raw SQL, which also lets the DB do the paging.
    /// </summary>
    private async Task<PaginatedResponse<UserProfileDto>> SearchUsersAsync(
        string search, int page, int pageSize, CancellationToken ct)
    {
        page = page < 1 ? 1 : page;
        pageSize = pageSize is < 1 or > 500 ? 50 : pageSize;

        // Enriched side: substring match (full scan of the smaller `users` table is cheap).
        // Escape %/_ so user input can't pivot into wildcards (requires the ESCAPE clause).
        var escaped = search.Replace("%", "\\%").Replace("_", "\\_");
        var substring = $"%{escaped}%";

        // Non-enriched side: PREFIX match only. SQLite can use an index for a LIKE prefix
        // solely when the column collates NOCASE *and* there's no ESCAPE clause — that's
        // what IX_organization_members_UserHandle_NoCase exists for. A substring there
        // would force a full scan of the ~12M-row snapshot table (tens of seconds). '%'
        // can't appear in a handle, so stripping it (not escaping) keeps the prefix clean;
        // an empty prefix becomes a guaranteed no-match rather than a match-all scan.
        var prefixTerm = search.Trim().Replace("%", "");
        var prefix = prefixTerm.Length == 0 ? "" : prefixTerm + "%";

        // Numeric term → also match by citizen id (enriched users + tracked entities).
        // -1 can never match a real citizen id (all are > 0), so a non-numeric term is a no-op here.
        var citizenId = int.TryParse(search.Trim(), out var cid) && cid > 0 ? cid : -1;

        // Shared UNION body. {0} = substring pattern (enriched), {1} = prefix pattern (members).
        const string union = @"
            SELECT CitizenId, UserHandle, DisplayName, UrlImage, Bio, Location, Enlisted, UpdatedAt, 1 AS IsEnriched
            FROM users
            WHERE UserHandle LIKE {0} ESCAPE '\' OR (DisplayName IS NOT NULL AND DisplayName LIKE {0} ESCAPE '\')
               OR CitizenId = {2}
            UNION ALL
            SELECT 0 AS CitizenId, m.UserHandle, m.DisplayName, m.UrlImage,
                   NULL AS Bio, NULL AS Location, NULL AS Enlisted, m.Timestamp AS UpdatedAt, 0 AS IsEnriched
            FROM organization_members m
            WHERE m.UserHandle LIKE {1}
              AND NOT EXISTS (SELECT 1 FROM users u WHERE u.UserHandle = m.UserHandle)
              AND NOT EXISTS (SELECT 1 FROM organization_members o
                              WHERE o.UserHandle = m.UserHandle AND o.Timestamp > m.Timestamp)
            UNION ALL
            SELECT COALESCE(e.CitizenId, 0) AS CitizenId, e.CurrentHandle AS UserHandle, e.DisplayName,
                   NULL AS UrlImage, NULL AS Bio, NULL AS Location, NULL AS Enlisted, e.UpdatedAt, 0 AS IsEnriched
            FROM tracked_entities e
            WHERE e.CurrentHandle IS NOT NULL
              AND (e.CurrentHandle LIKE {0} ESCAPE '\' OR (e.DisplayName IS NOT NULL AND e.DisplayName LIKE {0} ESCAPE '\'))
              AND NOT EXISTS (SELECT 1 FROM users u WHERE u.UserHandle = e.CurrentHandle)
            UNION ALL
            SELECT COALESCE(en.CitizenId, 0) AS CitizenId, en.CurrentHandle AS UserHandle, en.DisplayName,
                   NULL AS UrlImage, NULL AS Bio, NULL AS Location, NULL AS Enlisted, en.UpdatedAt, 0 AS IsEnriched
            FROM tracked_entities en
            WHERE en.CurrentHandle IS NOT NULL
              AND NOT (en.CurrentHandle LIKE {0} ESCAPE '\' OR (en.DisplayName IS NOT NULL AND en.DisplayName LIKE {0} ESCAPE '\'))
              AND EXISTS (SELECT 1 FROM entity_notes nt WHERE nt.TrackedEntityId = en.Id AND nt.Body LIKE {0} ESCAPE '\')
            UNION ALL
            SELECT ec.CitizenId, ec.CurrentHandle AS UserHandle, ec.DisplayName,
                   NULL AS UrlImage, NULL AS Bio, NULL AS Location, NULL AS Enlisted, ec.UpdatedAt, 0 AS IsEnriched
            FROM tracked_entities ec
            WHERE ec.CitizenId = {2} AND ec.CurrentHandle IS NOT NULL
              AND NOT EXISTS (SELECT 1 FROM users u WHERE u.UserHandle = ec.CurrentHandle)";

        var total = await _db.Database
            .SqlQueryRaw<int>($"SELECT COUNT(*) AS Value FROM ({union})", substring, prefix, citizenId)
            .SingleAsync(ct);

        var pageSql = $"SELECT * FROM ({union}) ORDER BY UserHandle COLLATE NOCASE LIMIT {{3}} OFFSET {{4}}";
        var items = await _db.Database
            .SqlQueryRaw<UserProfileDto>(pageSql, substring, prefix, citizenId, pageSize, (page - 1) * pageSize)
            .ToListAsync(ct);

        return PaginatedResponse<UserProfileDto>.Create(items, page, pageSize, total);
    }

    [HttpGet("{handle}")]
    public async Task<ActionResult<UserProfileDto>> GetByHandle(string handle, CancellationToken ct)
    {
        var user = await _userRepo.GetByHandleAsync(handle, ct)
            ?? throw new KeyNotFoundException($"User '{handle}' not found");
        return Ok(user.ToProfileDto());
    }

    [HttpGet("by-citizen-id/{id:int}")]
    public async Task<ActionResult<UserProfileDto>> GetByCitizenId(int id, CancellationToken ct)
    {
        var user = await _userRepo.GetByCitizenIdAsync(id, ct)
            ?? throw new KeyNotFoundException($"User with citizen ID {id} not found");
        return Ok(user.ToProfileDto());
    }

    [HttpGet("resolve/{handle}")]
    public async Task<ActionResult<UserResolveDto>> Resolve(string handle, CancellationToken ct)
    {
        // Try direct lookup first
        var user = await _userRepo.GetByHandleAsync(handle, ct);
        if (user is not null)
            return Ok(new UserResolveDto
            {
                CitizenId = user.CitizenId,
                CurrentHandle = user.UserHandle,
                RequestedHandle = handle,
                HandleChanged = false,
            });

        // Fallback: look in handle history
        var history = await _handleHistoryRepo.GetByHandleAsync(handle, ct);
        if (history is null)
            throw new KeyNotFoundException($"Handle '{handle}' not found");

        var currentUser = await _userRepo.GetByCitizenIdAsync(history.CitizenId, ct);
        if (currentUser is null)
            throw new KeyNotFoundException($"Handle '{handle}' not found");

        return Ok(new UserResolveDto
        {
            CitizenId = currentUser.CitizenId,
            CurrentHandle = currentUser.UserHandle,
            RequestedHandle = handle,
            HandleChanged = currentUser.UserHandle != handle,
        });
    }

    [HttpGet("{handle}/organizations")]
    public async Task<ActionResult<IReadOnlyList<OrganizationMemberDto>>> GetOrganizations(
        string handle,
        [FromQuery] bool include_inactive = false,
        CancellationToken ct = default)
    {
        var memberships = await _db.OrganizationMembers
            .AsNoTracking()
            .Where(m => m.UserHandle == handle)
            .GroupBy(m => m.OrgSid)
            .Select(g => g.OrderByDescending(m => m.Timestamp).First())
            .ToListAsync(ct);

        if (!include_inactive)
            memberships = memberships.Where(m => m.IsActive).ToList();

        // "Member since" = first snapshot in which this handle appeared in each org.
        // A plain GroupBy + Min aggregate (unlike the First() projection above) so it
        // stays a single translatable query.
        var firstSeen = await _db.OrganizationMembers
            .AsNoTracking()
            .Where(m => m.UserHandle == handle)
            .GroupBy(m => m.OrgSid)
            .Select(g => new { OrgSid = g.Key, First = g.Min(m => m.Timestamp) })
            .ToDictionaryAsync(x => x.OrgSid, x => x.First, ct);

        // Resolve the latest known name for each org so the frontend can show
        // "SID — Name" instead of just the SID.
        var orgSids = memberships.Select(m => m.OrgSid).Distinct().ToList();
        var orgNames = await _db.Organizations
            .AsNoTracking()
            .Where(o => orgSids.Contains(o.Sid))
            .GroupBy(o => o.Sid)
            .Select(g => g.OrderByDescending(o => o.Timestamp).First())
            .ToDictionaryAsync(o => o.Sid, o => o.Name, ct);

        return Ok(memberships
            .Select(m => m.ToDto(
                orgNames.GetValueOrDefault(m.OrgSid),
                firstSeen.TryGetValue(m.OrgSid, out var since) ? since : null))
            .ToList());
    }

    [HttpGet("{handle}/history")]
    public async Task<ActionResult<IReadOnlyList<UserHandleHistoryDto>>> GetHistory(string handle, CancellationToken ct)
    {
        var user = await _userRepo.GetByHandleAsync(handle, ct);
        int? citizenId = user?.CitizenId;

        if (citizenId is null)
        {
            var histEntry = await _handleHistoryRepo.GetByHandleAsync(handle, ct);
            citizenId = histEntry?.CitizenId;
        }

        if (citizenId is null)
            throw new KeyNotFoundException($"User '{handle}' not found");

        var history = await _handleHistoryRepo.GetByCitizenIdAsync(citizenId.Value, ct);
        return Ok(history.Select(h => h.ToDto()).ToList());
    }

    [HttpGet("{handle}/changes")]
    public async Task<ActionResult<IReadOnlyList<ChangeEventDto>>> GetChanges(
        string handle,
        [FromQuery] int limit = 100,
        CancellationToken ct = default)
    {
        var changes = await _changeRepo.GetByUserHandleAsync(handle, limit, ct);
        return Ok(changes.Select(c => c.ToDto()).ToList());
    }
}
