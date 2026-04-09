using Collector.Api.Dtos.Changes;
using Collector.Api.Dtos.Organizations;
using Collector.Api.Dtos.Users;
using Collector.Models;

namespace Collector.Api.Extensions;

/// <summary>
/// Single source of truth for entity → DTO conversions used by the API controllers.
/// Before this existed each controller had its own <c>MapUser</c> / <c>MapOrg</c> /
/// <c>MapChange</c> local static, which caused multiple subtle divergences.
/// </summary>
public static class DtoMappingExtensions
{
    public static UserProfileDto ToProfileDto(this User u) => new()
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

    public static OrganizationMemberDto ToDto(this OrganizationMember m) => new()
    {
        OrgSid = m.OrgSid,
        UserHandle = m.UserHandle,
        CitizenId = m.CitizenId,
        DisplayName = m.DisplayName,
        Rank = m.Rank,
        UrlImage = m.UrlImage,
        Timestamp = m.Timestamp,
        IsActive = m.IsActive,
    };

    public static ChangeEventDto ToDto(this ChangeEvent e) => new()
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

    public static UserHandleHistoryDto ToDto(this UserHandleHistory h) => new()
    {
        UserHandle = h.UserHandle,
        FirstSeen = h.FirstSeen,
        LastSeen = h.LastSeen,
    };
}
