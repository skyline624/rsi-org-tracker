using System.Text;
using Collector.Api.Dtos.Organizations;
using Collector.Api.Errors;
using Collector.Data;
using Collector.Models;
using Microsoft.EntityFrameworkCore;

namespace Collector.Api.Services;

/// <summary>Organization queries shared by the site (OrganizationsController) and the Discord bot.</summary>
public sealed class OrganizationLookupService(TrackerDbContext db)
{
    // Efficient "latest snapshot per org" using INNER JOIN with MAX(Timestamp). The
    // list needs no long text: they are not read (the detail page fetches them).
    public IQueryable<Organization> LatestOrgs() =>
        db.Organizations.FromSqlRaw("""
            SELECT o.Id, o.Sid, o.Timestamp, o.Name, o.UrlImage, o.UrlCorpo,
                   o.Archetype, o.Lang, o.Commitment, o.Recruiting, o.Roleplay,
                   o.MembersCount, NULL AS Description, NULL AS History,
                   NULL AS Manifesto, NULL AS Charter,
                   o.FocusPrimaryName, o.FocusPrimaryImage, o.FocusSecondaryName,
                   o.FocusSecondaryImage, o.ContentCollected, o.Source
            FROM organizations AS o
            INNER JOIN (
                SELECT Sid, MAX(Timestamp) AS MaxTs
                FROM organizations GROUP BY Sid
            ) AS g ON o.Sid = g.Sid AND o.Timestamp = g.MaxTs
            """);

    /// <summary>Small name/SID lookup for explicit organization selection, independent of list pagination.</summary>
    public async Task<IReadOnlyList<OrganizationSuggestionDto>> SuggestAsync(string? query, CancellationToken ct)
    {
        if (query?.Length > 100)
            throw new ValidationException("La recherche doit contenir au maximum 100 caractères.");

        // Discord often uses mathematical letters and decorative separators. FormKC
        // turns those letters into ordinary ones; punctuation is irrelevant for lookup.
        var normalized = (query ?? "").Normalize(NormalizationForm.FormKC).Trim();
        var compact = new StringBuilder();
        foreach (var rune in normalized.EnumerateRunes())
            if (Rune.IsLetterOrDigit(rune)) compact.Append(rune);
        if (compact.Length == 0) return Array.Empty<OrganizationSuggestionDto>();

        // Only letters/digits enter LIKE, so user input cannot become a wildcard.
        var text = compact.ToString();
        var pattern = $"%{text}%";
        var prefix = $"{text}%";
        var sid = normalized.ToUpperInvariant();
        var candidates = LatestOrgs().AsNoTracking().Select(o => new
        {
            o.Sid,
            o.Name,
            CompactSid = o.Sid.Replace("-", "").Replace("_", ""),
            CompactName = o.Name.Replace(" ", "").Replace("-", "").Replace("_", "")
                .Replace("'", "").Replace("’", "").Replace(".", "").Replace("/", "")
                .Replace("\u00a0", "").Replace("–", "").Replace("—", "").Replace("‑", ""),
        });
        var items = await candidates
            .Where(o => EF.Functions.Like(o.CompactSid, pattern) || EF.Functions.Like(o.CompactName, pattern))
            .OrderByDescending(o => o.Sid == sid)
            .ThenByDescending(o => EF.Functions.Like(o.CompactName, text))
            .ThenByDescending(o => EF.Functions.Like(o.CompactSid, prefix))
            .ThenByDescending(o => EF.Functions.Like(o.CompactName, prefix))
            .ThenBy(o => o.Sid)
            .Take(10)
            .Select(o => new OrganizationSuggestionDto(o.Sid, o.Name))
            .ToListAsync(ct);
        return items;
    }
}
