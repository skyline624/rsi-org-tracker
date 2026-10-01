using System.Text.Json;

namespace Collector.Discord;

/// <summary>A role as a roles_changed event stored it: its id and its name at the time.</summary>
public sealed record EventRole(string Id, string Name);

/// <summary>
/// Reads the two role lists stored as JSON: discord_members.RoleIdsJson (an array of role id
/// strings) and the values of roles_changed events (an array of {"id","name"} objects, spec
/// § 8). A malformed value reads as an empty list, so one bad row never fails a read.
/// </summary>
public static class DiscordRoleLists
{
    /// <summary>The role ids of a RoleIdsJson value, in stored order; non-string entries are skipped.</summary>
    public static IReadOnlyList<string> ParseRoleIds(string? roleIdsJson)
    {
        var ids = new List<string>();
        foreach (var item in ArrayItems(roleIdsJson))
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } id) ids.Add(id);
        }
        return ids;
    }

    /// <summary>The roles of a roles_changed value; an entry without a string id is skipped, a missing name reads as "".</summary>
    public static IReadOnlyList<EventRole> ParseEventRoles(string? eventValue)
    {
        var roles = new List<EventRole>();
        foreach (var item in ArrayItems(eventValue))
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("id", out var id)
                || id.ValueKind != JsonValueKind.String
                || id.GetString() is not { Length: > 0 } roleId)
            {
                continue;
            }
            var name = item.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "";
            roles.Add(new EventRole(roleId, name));
        }
        return roles;
    }

    private static List<JsonElement> ArrayItems(string? json)
    {
        var items = new List<JsonElement>();
        if (string.IsNullOrWhiteSpace(json)) return items;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return items;
            // Cloned: the elements outlive the document.
            items.AddRange(document.RootElement.EnumerateArray().Select(e => e.Clone()));
            return items;
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
