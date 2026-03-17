using Collector.Api.Dtos.Common;
using Collector.Api.Dtos.Users;
using Collector.Api.Dtos.Changes;
using Collector.Api.Dtos.Organizations;
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
        var query = _db.Users.AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(u =>
                u.UserHandle.Contains(search) ||
                (u.DisplayName != null && u.DisplayName.Contains(search)));

        var total = await query.CountAsync(ct);
        var users = await query
            .OrderBy(u => u.UserHandle)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return Ok(new PaginatedResponse<UserProfileDto>
        {
            Items = users.Select(MapUser).ToList(),
            Total = total,
            Page = page,
            PageSize = pageSize,
        });
    }

    [HttpGet("{handle}")]
    public async Task<ActionResult<UserProfileDto>> GetByHandle(string handle, CancellationToken ct)
    {
        var user = await _userRepo.GetByHandleAsync(handle, ct)
            ?? throw new KeyNotFoundException($"User '{handle}' not found");
        return Ok(MapUser(user));
    }

    [HttpGet("by-citizen-id/{id:int}")]
    public async Task<ActionResult<UserProfileDto>> GetByCitizenId(int id, CancellationToken ct)
    {
        var user = await _userRepo.GetByCitizenIdAsync(id, ct)
            ?? throw new KeyNotFoundException($"User with citizen ID {id} not found");
        return Ok(MapUser(user));
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
            .Where(m => m.UserHandle == handle)
            .GroupBy(m => m.OrgSid)
            .Select(g => g.OrderByDescending(m => m.Timestamp).First())
            .ToListAsync(ct);

        if (!include_inactive)
            memberships = memberships.Where(m => m.IsActive).ToList();

        return Ok(memberships.Select(m => new OrganizationMemberDto
        {
            UserHandle = m.UserHandle,
            CitizenId = m.CitizenId,
            DisplayName = m.DisplayName,
            Rank = m.Rank,
            UrlImage = m.UrlImage,
            Timestamp = m.Timestamp,
            IsActive = m.IsActive,
        }).ToList());
    }

    [HttpGet("{handle}/history")]
    public async Task<ActionResult<IReadOnlyList<UserHandleHistoryDto>>> GetHistory(string handle, CancellationToken ct)
    {
        // Resolve citizen ID
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
        return Ok(history.Select(h => new UserHandleHistoryDto
        {
            UserHandle = h.UserHandle,
            FirstSeen = h.FirstSeen,
            LastSeen = h.LastSeen,
        }).ToList());
    }

    [HttpGet("{handle}/changes")]
    public async Task<ActionResult<IReadOnlyList<ChangeEventDto>>> GetChanges(
        string handle,
        [FromQuery] int limit = 100,
        CancellationToken ct = default)
    {
        var changes = await _changeRepo.GetByUserHandleAsync(handle, limit, ct);
        return Ok(changes.Select(MapChange).ToList());
    }

    private static UserProfileDto MapUser(Collector.Models.User u) => new()
    {
        CitizenId = u.CitizenId,
        UserHandle = u.UserHandle,
        DisplayName = u.DisplayName,
        UrlImage = u.UrlImage,
        Bio = u.Bio,
        Location = u.Location,
        Enlisted = u.Enlisted,
        UpdatedAt = u.UpdatedAt,
    };

    private static ChangeEventDto MapChange(Collector.Models.ChangeEvent e) => new()
    {
        Id = e.Id,
        Timestamp = e.Timestamp,
        EntityType = e.EntityType,
        EntityId = e.EntityId,
        ChangeType = e.ChangeType,
        OldValue = e.OldValue,
        NewValue = e.NewValue,
        OrgSid = e.OrgSid,
        UserHandle = e.UserHandle,
    };
}
