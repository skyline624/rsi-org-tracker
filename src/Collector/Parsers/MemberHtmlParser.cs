using HtmlAgilityPack;
using Collector.Dtos;
using Microsoft.Extensions.Logging;

namespace Collector.Parsers;

/// <summary>
/// Parses member list HTML from RSI API responses.
/// </summary>
public class MemberHtmlParser
{
    private readonly ILogger<MemberHtmlParser> _logger;

    public MemberHtmlParser(ILogger<MemberHtmlParser> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Parses member list HTML into MemberData objects.
    /// </summary>
    public IReadOnlyList<MemberData> ParseMembers(string html, string orgSid)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var rows = doc.DocumentNode.SelectNodes("//li[contains(@class, 'member-item')]");
        if (rows == null || rows.Count == 0)
        {
            // Try alternative: any list item with a citizen link
            rows = doc.DocumentNode.SelectNodes("//li[.//a[contains(@href, '/citizens/')]]");
        }

        if (rows == null || rows.Count == 0)
        {
            _logger.LogWarning("No member rows found in HTML for org {OrgSid}", orgSid);
            return Array.Empty<MemberData>();
        }

        var members = new List<MemberData>();

        foreach (var row in rows)
        {
            try
            {
                var member = ParseMemberRow(row, orgSid);
                if (member != null)
                {
                    members.Add(member);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error parsing member row for org {OrgSid}", orgSid);
            }
        }

        return members;
    }

    private MemberData? ParseMemberRow(HtmlNode row, string orgSid)
    {
        // Extract handle from link
        var handle = ExtractHandle(row);
        if (string.IsNullOrEmpty(handle))
        {
            return null;
        }

        // Extract citizen_id from data attribute or link
        var citizenId = ExtractCitizenId(row);

        // Extract display name
        var displayName = ExtractDisplayName(row);

        // Extract rank
        var rank = ExtractRank(row);

        // Extract roles
        var roles = ExtractRoles(row);

        // Extract avatar URL
        var urlImage = ExtractAvatarUrl(row);

        return new MemberData
        {
            OrgSid = orgSid,
            Handle = handle,
            CitizenId = citizenId,
            DisplayName = displayName,
            Rank = rank,
            Roles = roles,
            UrlImage = urlImage
        };
    }

    private string? ExtractHandle(HtmlNode row)
    {
        // Try citizen link
        var link = row.SelectSingleNode(".//a[contains(@href, '/citizens/')]");
        var href = link?.GetAttributeValue("href", "");

        if (string.IsNullOrEmpty(href))
        {
            return null;
        }

        // Extract handle from URL like /citizens/TestHandle
        var parts = href.Split('/');
        return parts.Length > 0 ? parts[^1] : null;
    }

    private int? ExtractCitizenId(HtmlNode row)
    {
        // Try data-citizen-id attribute
        var citizenIdStr = row.GetAttributeValue("data-citizen-id", "");
        if (!string.IsNullOrEmpty(citizenIdStr) && int.TryParse(citizenIdStr, out var citizenId))
        {
            return citizenId;
        }

        // Try citizen link with numeric ID
        var link = row.SelectSingleNode(".//a[contains(@href, '/citizens/')]");
        var href = link?.GetAttributeValue("href", "");
        if (href != null)
        {
            // Some links have numeric IDs like /citizens/123456
            var parts = href.Split('/');
            var lastPart = parts[^1];
            if (int.TryParse(lastPart, out citizenId))
            {
                return citizenId;
            }
        }

        // Try extracting from text content
        var text = row.InnerText ?? string.Empty;
        if (!string.IsNullOrEmpty(text))
        {
            // Look for patterns like "#123456"
            var hashIndex = text.IndexOf('#');
            if (hashIndex >= 0)
            {
                var numberPart = text.Substring(hashIndex + 1).Split(' ')[0];
                if (int.TryParse(numberPart, out citizenId))
                {
                    return citizenId;
                }
            }
        }

        return null;
    }

    private string? ExtractDisplayName(HtmlNode row)
    {
        var nameNode = row.SelectSingleNode(".//*[contains(@class, 'name')]");
        return nameNode?.InnerText?.Trim();
    }

    private string? ExtractRank(HtmlNode row)
    {
        var rankNode = row.SelectSingleNode(".//*[contains(@class, 'rank')]|.//*[contains(@class, 'title')]");
        return rankNode?.InnerText?.Trim();
    }

    private string[]? ExtractRoles(HtmlNode row)
    {
        var rolesNodes = row.SelectNodes(".//*[contains(@class, 'role')]|.//*[contains(@class, 'badge')]");
        if (rolesNodes == null || rolesNodes.Count == 0)
        {
            return null;
        }

        return rolesNodes
            .Select(n => n.InnerText?.Trim())
            .Where(r => !string.IsNullOrEmpty(r))
            .Select(r => r!)
            .ToArray();
    }

    private string? ExtractAvatarUrl(HtmlNode row)
    {
        var img = row.SelectSingleNode(".//img[contains(@class, 'avatar')]|.//img[contains(@src, 'avatar')]");
        return img?.GetAttributeValue("src", "");
    }
}