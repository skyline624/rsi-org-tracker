using System.Text.Json;
using Collector.Dtos;
using Collector.Models;
using Microsoft.Extensions.Logging;

namespace Collector.Services;

/// <summary>
/// Snapshot of organization data for change detection.
/// </summary>
public record OrganizationSnapshot
{
    public string Sid { get; init; } = null!;
    public string Name { get; init; } = null!;
    public string? Archetype { get; init; }
    public string? Commitment { get; init; }
    public bool? Recruiting { get; init; }
    public bool? Roleplay { get; init; }
    public string? Lang { get; init; }
    public int Members { get; init; }
}

/// <summary>
/// Snapshot of member data for change detection.
/// </summary>
public record MemberSnapshot
{
    /// <summary>
    /// Permanent RSI citizen ID (primary identifier).
    /// </summary>
    public int? CitizenId { get; init; }

    /// <summary>
    /// User handle (can change over time).
    /// </summary>
    public string Handle { get; init; } = null!;

    /// <summary>
    /// Member's rank within the organization.
    /// </summary>
    public string? Rank { get; init; }

    /// <summary>
    /// Member's roles within the organization.
    /// </summary>
    public string[]? Roles { get; init; }
}

/// <summary>
/// Interface for detecting changes in organizations and members.
/// </summary>
public interface IChangeDetector
{
    /// <summary>
    /// Detects changes between two organization snapshots.
    /// </summary>
    IReadOnlyList<ChangeEvent> DetectOrganizationChanges(
        OrganizationSnapshot? previous,
        OrganizationSnapshot current,
        string orgSid);

    /// <summary>
    /// Detects changes between two member collections.
    /// </summary>
    IReadOnlyList<ChangeEvent> DetectMemberChanges(
        string orgSid,
        IReadOnlyList<MemberSnapshot> previous,
        IReadOnlyList<MemberSnapshot> current);

    /// <summary>
    /// Creates snapshots from member collection log entries.
    /// </summary>
    IReadOnlyList<MemberSnapshot> CreateSnapshotsFromLogs(
        IReadOnlyList<MemberCollectionLog> logs);
}

/// <summary>
/// Detects changes in organizations and members.
/// </summary>
public class ChangeDetector : IChangeDetector
{
    private readonly ILogger<ChangeDetector> _logger;

    public ChangeDetector(ILogger<ChangeDetector> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<ChangeEvent> DetectOrganizationChanges(
        OrganizationSnapshot? previous,
        OrganizationSnapshot current,
        string orgSid)
    {
        var events = new List<ChangeEvent>();
        var timestamp = DateTime.UtcNow;

        // First snapshot - no changes to detect
        if (previous == null)
        {
            _logger.LogDebug("First snapshot for organization {OrgSid}", orgSid);
            return events;
        }

        // Detect name change
        if (previous.Name != current.Name)
        {
            events.Add(CreateEvent(
                "organization", orgSid, "name_changed",
                previous.Name, current.Name,
                orgSid, null, timestamp));
        }

        // Detect member count change
        if (previous.Members != current.Members)
        {
            events.Add(CreateEvent(
                "organization", orgSid, "member_count_changed",
                previous.Members.ToString(), current.Members.ToString(),
                orgSid, null, timestamp));
        }

        // Detect recruiting status change
        if (previous.Recruiting != current.Recruiting)
        {
            events.Add(CreateEvent(
                "organization", orgSid, "recruiting_changed",
                previous.Recruiting?.ToString(), current.Recruiting?.ToString(),
                orgSid, null, timestamp));
        }

        // Detect roleplay status change
        if (previous.Roleplay != current.Roleplay)
        {
            events.Add(CreateEvent(
                "organization", orgSid, "roleplay_changed",
                previous.Roleplay?.ToString(), current.Roleplay?.ToString(),
                orgSid, null, timestamp));
        }

        // Detect archetype change
        if (previous.Archetype != current.Archetype)
        {
            events.Add(CreateEvent(
                "organization", orgSid, "archetype_changed",
                previous.Archetype, current.Archetype,
                orgSid, null, timestamp));
        }

        // Detect language change
        if (previous.Lang != current.Lang)
        {
            events.Add(CreateEvent(
                "organization", orgSid, "language_changed",
                previous.Lang, current.Lang,
                orgSid, null, timestamp));
        }

        return events;
    }

    public IReadOnlyList<ChangeEvent> DetectMemberChanges(
        string orgSid,
        IReadOnlyList<MemberSnapshot> previous,
        IReadOnlyList<MemberSnapshot> current)
    {
        var events = new List<ChangeEvent>();
        var timestamp = DateTime.UtcNow;

        // Index by citizen_id (priority) and handle
        var prevByCitizenId = previous
            .Where(m => m.CitizenId.HasValue)
            .ToDictionary(m => m.CitizenId!.Value);

        var prevByHandle = previous
            .ToDictionary(m => m.Handle, StringComparer.OrdinalIgnoreCase);

        var currByCitizenId = current
            .Where(m => m.CitizenId.HasValue)
            .ToDictionary(m => m.CitizenId!.Value);

        var currByHandle = current
            .ToDictionary(m => m.Handle, StringComparer.OrdinalIgnoreCase);

        // Detect leaves
        foreach (var prevMember in previous)
        {
            bool stillPresent = false;
            MemberSnapshot? matchedCurrent = null;

            // Priority 1: citizen_id match (most reliable)
            if (prevMember.CitizenId.HasValue && currByCitizenId.TryGetValue(prevMember.CitizenId.Value, out var byCitizenId))
            {
                stillPresent = true;
                matchedCurrent = byCitizenId;
            }
            // Priority 2: handle match with same citizen_id
            else if (currByHandle.TryGetValue(prevMember.Handle, out var byHandle))
            {
                // If citizen_ids match (both null or same value), it's the same user
                stillPresent = prevMember.CitizenId == byHandle.CitizenId;
                matchedCurrent = stillPresent ? byHandle : null;
            }

            if (!stillPresent)
            {
                events.Add(CreateEvent(
                    "member", prevMember.Handle, "member_left",
                    JsonSerializer.Serialize(prevMember), null,
                    orgSid, prevMember.Handle, timestamp));
            }
            else if (matchedCurrent != null)
            {
                // Check for rank change
                if (prevMember.Rank != matchedCurrent.Rank)
                {
                    events.Add(CreateEvent(
                        "member", prevMember.Handle, "rank_changed",
                        prevMember.Rank, matchedCurrent.Rank,
                        orgSid, prevMember.Handle, timestamp));
                }

                // Check for roles change
                if (!RolesEqual(prevMember.Roles, matchedCurrent.Roles))
                {
                    events.Add(CreateEvent(
                        "member", prevMember.Handle, "roles_changed",
                        JsonSerializer.Serialize(prevMember.Roles),
                        JsonSerializer.Serialize(matchedCurrent.Roles),
                        orgSid, prevMember.Handle, timestamp));
                }
            }
        }

        // Detect joins
        foreach (var currMember in current)
        {
            bool isNew = true;

            // Check if this citizen_id existed before
            if (currMember.CitizenId.HasValue && prevByCitizenId.ContainsKey(currMember.CitizenId.Value))
            {
                isNew = false;
            }
            // Check if this handle existed with same citizen_id
            else if (prevByHandle.TryGetValue(currMember.Handle, out var prevMember))
            {
                isNew = prevMember.CitizenId != currMember.CitizenId;
            }

            if (isNew)
            {
                events.Add(CreateEvent(
                    "member", currMember.Handle, "member_joined",
                    null, JsonSerializer.Serialize(currMember),
                    orgSid, currMember.Handle, timestamp));
            }
        }

        if (events.Count > 0)
        {
            _logger.LogInformation(
                "Detected {Changes} changes for organization {OrgSid}: {Joins} joins, {Leaves} leaves",
                events.Count, orgSid,
                events.Count(e => e.ChangeType == "member_joined"),
                events.Count(e => e.ChangeType == "member_left"));
        }

        return events;
    }

    public IReadOnlyList<MemberSnapshot> CreateSnapshotsFromLogs(IReadOnlyList<MemberCollectionLog> logs)
    {
        return logs
            .Select(log => new MemberSnapshot
            {
                CitizenId = log.CitizenId,
                Handle = log.UserHandle,
                Rank = log.Rank,
                Roles = log.RolesJson != null
                    ? JsonSerializer.Deserialize<string[]>(log.RolesJson)
                    : null
            })
            .ToList();
    }

    private static ChangeEvent CreateEvent(
        string entityType,
        string entityId,
        string changeType,
        string? oldValue,
        string? newValue,
        string? orgSid,
        string? userHandle,
        DateTime timestamp)
    {
        return new ChangeEvent
        {
            Timestamp = timestamp,
            EntityType = entityType,
            EntityId = entityId,
            ChangeType = changeType,
            OldValue = oldValue,
            NewValue = newValue,
            OrgSid = orgSid,
            UserHandle = userHandle
        };
    }

    private static bool RolesEqual(string[]? roles1, string[]? roles2)
    {
        if (roles1 == null && roles2 == null) return true;
        if (roles1 == null || roles2 == null) return false;
        if (roles1.Length != roles2.Length) return false;

        var sorted1 = roles1.OrderBy(r => r).ToArray();
        var sorted2 = roles2.OrderBy(r => r).ToArray();

        return sorted1.SequenceEqual(sorted2);
    }
}