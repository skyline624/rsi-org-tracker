using System.Text.Json;
using Collector.Dtos;
using Collector.Models;
using Collector.Parsers;
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

    private static readonly System.Text.RegularExpressions.Regex Whitespace = new(@"\s+");

    /// <summary>A stored value as the profile parser writes it today.</summary>
    private static string? Normalized(string? value)
        => value == null ? null : Whitespace.Replace(HtmlText.Decode(value), " ").Trim();

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

        // A change of a known value only: most stored users have no display name or
        // location (the old parser could not read them), and the first value read is
        // not an event. Stored values are compared the way the parser now writes them:
        // some were stored HTML-encoded or with runs of whitespace.
        if (Normalized(existing.DisplayName) != newData.DisplayName
            && !string.IsNullOrEmpty(existing.DisplayName) && !string.IsNullOrEmpty(newData.DisplayName))
        {
            events.Add(CreateEvent(
                "user", existing.CitizenId.ToString(), "display_name_changed",
                existing.DisplayName, newData.DisplayName,
                null, existing.UserHandle, timestamp));
        }

        if (Normalized(existing.Location) != newData.Location
            && !string.IsNullOrEmpty(existing.Location) && !string.IsNullOrEmpty(newData.Location))
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