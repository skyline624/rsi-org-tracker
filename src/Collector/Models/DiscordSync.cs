namespace Collector.Models;

/// <summary>One accepted sync of a server: who sent it, how it was collected and what it produced.</summary>
public class DiscordSync
{
    public long Id { get; set; }

    public string GuildId { get; set; } = null!;

    /// <summary>Owner of the discord:ingest key (soft reference into api.db).</summary>
    public long SubmittedByApiUserId { get; set; }

    public string SubmittedByUsername { get; set; } = null!;

    /// <summary>Server clock when the request was received.</summary>
    public DateTime ReceivedAt { get; set; }

    /// <summary>ReceivedAt minus the collection duration: when the collection started.</summary>
    public DateTime CollectedAt { get; set; }

    /// <summary>The plugin's clock, informative only.</summary>
    public DateTime DeclaredCollectedAt { get; set; }

    /// <summary>See <see cref="DiscordSyncMethods"/>.</summary>
    public string Method { get; set; } = null!;

    /// <summary>The plugin's own completeness flag.</summary>
    public bool DeclaredComplete { get; set; }

    /// <summary>Completeness recomputed by the server; a mass departure never downgrades it.</summary>
    public bool IsComplete { get; set; }

    public bool IsBaseline { get; set; }

    public bool MassDepartureDetected { get; set; }

    /// <summary>Member count the plugin expected (member search only).</summary>
    public int? ExpectedCount { get; set; }

    /// <summary>Members in the request, opted-out ones included.</summary>
    public int CollectedCount { get; set; }

    public int OptedOutCount { get; set; }

    /// <summary>References to roles missing from the request (deleted during the collection).</summary>
    public int UnknownRoleRefCount { get; set; }

    public int EventCount { get; set; }

    public string PluginVersion { get; set; } = null!;
}

/// <summary>How the plugin collected a server's members. Only member search can be complete.</summary>
public static class DiscordSyncMethods
{
    public const string MemberSearch = "member-search", RoleMembers = "role-members", Cache = "cache";

    public static bool IsValid(string? m) => m is MemberSearch or RoleMembers or Cache;
}
