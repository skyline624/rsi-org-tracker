using System.Text.Json;
using Collector.Dtos;
using Collector.Models;
using Microsoft.Extensions.Logging;

namespace Collector.Services;

/// <summary>
/// Interface for detecting user profile changes.
/// </summary>
public interface IUserChangeDetector
{
    /// <summary>
    /// Detects changes between existing user and new profile data.
    /// </summary>
    IReadOnlyList<ChangeEvent> DetectUserChanges(User existing, UserProfileData newData);
}

/// <summary>
/// Detects changes in user profiles.
/// </summary>
public class UserChangeDetector : IUserChangeDetector
{
    private readonly ILogger<UserChangeDetector> _logger;

    public UserChangeDetector(ILogger<UserChangeDetector> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<ChangeEvent> DetectUserChanges(User existing, UserProfileData newData)
    {
        var events = new List<ChangeEvent>();
        var timestamp = DateTime.UtcNow;

        // Detect handle change (important for tracking)
        if (existing.UserHandle != newData.Handle)
        {
            _logger.LogInformation(
                "User handle changed: {OldHandle} -> {NewHandle} (citizen_id: {CitizenId})",
                existing.UserHandle, newData.Handle, newData.CitizenId);

            events.Add(CreateEvent(
                "user", existing.CitizenId.ToString(), "handle_changed",
                existing.UserHandle, newData.Handle,
                null, existing.UserHandle, timestamp));
        }

        // Detect display name change
        if (existing.DisplayName != newData.DisplayName && !string.IsNullOrEmpty(newData.DisplayName))
        {
            events.Add(CreateEvent(
                "user", existing.CitizenId.ToString(), "display_name_changed",
                existing.DisplayName, newData.DisplayName,
                null, existing.UserHandle, timestamp));
        }

        // Detect location change
        if (existing.Location != newData.Location && !string.IsNullOrEmpty(newData.Location))
        {
            events.Add(CreateEvent(
                "user", existing.CitizenId.ToString(), "location_changed",
                existing.Location, newData.Location,
                null, existing.UserHandle, timestamp));
        }

        // Note: Bio changes are not tracked as events (can be frequent)

        return events;
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
}