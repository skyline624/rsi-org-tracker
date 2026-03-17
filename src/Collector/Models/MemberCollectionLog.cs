namespace Collector.Models;

/// <summary>
/// Lightweight log of member presence during collection runs.
/// Used for detecting joins and leaves by comparing consecutive runs.
/// </summary>
public class MemberCollectionLog
{
    public long Id { get; set; }

    /// <summary>
    /// Organization symbol for this collection run.
    /// </summary>
    public string OrgSid { get; set; } = null!;

    /// <summary>
    /// When this collection run occurred.
    /// </summary>
    public DateTime CollectionTime { get; set; }

    /// <summary>
    /// Permanent RSI citizen ID (preferred for identification).
    /// </summary>
    public int? CitizenId { get; set; }

    /// <summary>
    /// User's RSI handle (fallback if citizen_id not available).
    /// </summary>
    public string UserHandle { get; set; } = null!;

    /// <summary>
    /// Member's rank at collection time. Used to detect rank changes.
    /// </summary>
    public string? Rank { get; set; }

    /// <summary>
    /// Member's roles as JSON at collection time. Used to detect role changes.
    /// </summary>
    public string? RolesJson { get; set; }
}