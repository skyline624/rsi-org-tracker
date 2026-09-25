namespace Collector.Models;

/// <summary>
/// Listing fields of an organization's latest snapshot, without the long texts
/// (description, history, manifesto, charter) that would cost gigabytes over ~100k orgs.
/// </summary>
public sealed record OrganizationListing(
    string Sid,
    string Name,
    string? Archetype,
    string? Lang,
    string? Commitment,
    bool? Recruiting,
    bool? Roleplay,
    int MembersCount,
    DateTime Timestamp,
    string? UrlImage = null,
    string? UrlCorpo = null);
