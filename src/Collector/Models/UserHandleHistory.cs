namespace Collector.Models;

/// <summary>
/// Tracks handle changes for users over time.
/// citizen_id is permanent, but handles can change.
/// </summary>
public class UserHandleHistory
{
    public long Id { get; set; }

    /// <summary>
    /// Permanent RSI citizen ID.
    /// </summary>
    public int CitizenId { get; set; }

    /// <summary>
    /// Handle value at this point in time.
    /// </summary>
    public string UserHandle { get; set; } = null!;

    /// <summary>
    /// When this handle was first observed.
    /// </summary>
    public DateTime FirstSeen { get; set; }

    /// <summary>
    /// When this handle was last observed.
    /// </summary>
    public DateTime LastSeen { get; set; }
}