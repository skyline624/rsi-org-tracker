using Collector.Api.Errors;
using Collector.Api.Dtos.Common;
using Collector.Api.Dtos.Users;
using Collector.Api.Dtos.Changes;
using Collector.Api.Dtos.Organizations;
using Collector.Api.Extensions;
using Collector.Api.Services;
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
    private readonly UserLookupService _lookup;
    private readonly TrackerDbContext _db;

    public UsersController(
        IUserRepository userRepo,
        IUserHandleHistoryRepository handleHistoryRepo,
        IOrganizationMemberRepository memberRepo,
        IChangeEventRepository changeRepo,
        UserLookupService lookup,
        TrackerDbContext db)
    {
        _lookup = lookup;
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
        page = Paging.Page(page);
        pageSize = Paging.PageSize(pageSize);

        // No search term: list enriched citizens only. A global listing of the ~600k
        // non-enriched roster handles would be huge and meaningless, so we keep the
        // default directory to real profiles (fast: single index-free scan of `users`).
        if (string.IsNullOrWhiteSpace(search))
        {
            var all = _db.Users.AsNoTracking().OrderBy(u => u.UserHandle);
            return Ok(await all.ToPaginatedAsync(page, pageSize, u => u.ToProfileDto(), ct));
        }

        return Ok(await _lookup.SearchAsync(search, page, pageSize, ct));
    }

    [HttpGet("{handle}")]
    public async Task<ActionResult<UserProfileDto>> GetByHandle(string handle, CancellationToken ct)
    {
        var user = await _userRepo.GetByHandleAsync(handle, ct)
            ?? throw new NotFoundException($"User '{handle}' not found");
        return Ok(user.ToProfileDto());
    }

    [HttpGet("by-citizen-id/{id:int}")]
    public async Task<ActionResult<UserProfileDto>> GetByCitizenId(int id, CancellationToken ct)
    {
        var user = await _userRepo.GetByCitizenIdAsync(id, ct)
            ?? throw new NotFoundException($"User with citizen ID {id} not found");
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
            throw new NotFoundException($"Handle '{handle}' not found");

        var currentUser = await _userRepo.GetByCitizenIdAsync(history.CitizenId, ct);
        if (currentUser is null)
            throw new NotFoundException($"Handle '{handle}' not found");

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
        var rows = await _lookup.GetMembershipsAsync(handle, include_inactive, ct);
        return Ok(rows.Select(r => r.Latest.ToDto(r.OrgName, r.FirstSeen)).ToList());
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
            throw new NotFoundException($"User '{handle}' not found");

        var history = await _handleHistoryRepo.GetByCitizenIdAsync(citizenId.Value, ct);
        return Ok(history.Select(h => h.ToDto()).ToList());
    }

    [HttpGet("{handle}/changes")]
    public async Task<ActionResult<IReadOnlyList<ChangeEventDto>>> GetChanges(
        string handle,
        [FromQuery] int limit = 100,
        CancellationToken ct = default)
    {
        var changes = await _changeRepo.GetByUserHandleAsync(handle, Paging.Limit(limit), ct);
        return Ok(changes.Select(c => c.ToDto()).ToList());
    }
}
