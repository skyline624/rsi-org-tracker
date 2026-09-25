namespace Collector.Models;

/// <summary>
/// An organization's roster counters as RSI reported them. A row is written only
/// when one of the counters differs from the organization's previous row.
/// </summary>
public class OrgMemberCount
{
    public long Id { get; set; }

    public string OrgSid { get; set; } = null!;

    public DateTime CollectedAt { get; set; }

    /// <summary>RSI's totalrows: every member, masked ones included.</summary>
    public int TotalRows { get; set; }

    /// <summary>Visible rows (org-visibility-V). Null when the roster could not be read in full.</summary>
    public int? VisibleCount { get; set; }

    /// <summary>Redacted rows (org-visibility-R). Null when the roster could not be read in full.</summary>
    public int? RedactedCount { get; set; }

    /// <summary>Hidden-affiliation rows (org-visibility-H). Null when the roster could not be read in full.</summary>
    public int? HiddenCount { get; set; }
}
