using HtmlAgilityPack;
using Collector.Dtos;
using Microsoft.Extensions.Logging;

namespace Collector.Parsers;

/// <summary>
/// Parses organization HTML from RSI API responses.
/// </summary>
public class OrganizationHtmlParser
{
    private readonly ILogger<OrganizationHtmlParser> _logger;

    public OrganizationHtmlParser(ILogger<OrganizationHtmlParser> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Parses organization list HTML into OrganizationData objects.
    /// </summary>
    public IReadOnlyList<OrganizationData> ParseOrganizations(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var cells = doc.DocumentNode.SelectNodes("//div[contains(@class, 'org-cell')]");
        if (cells == null || cells.Count == 0)
        {
            // Try alternative selectors
            cells = doc.DocumentNode.SelectNodes("//div[contains(@class, 'orgs-item')]");
        }

        if (cells == null || cells.Count == 0)
        {
            _logger.LogWarning("No organization cells found in HTML");
            return Array.Empty<OrganizationData>();
        }

        var organizations = new List<OrganizationData>();

        foreach (var cell in cells)
        {
            try
            {
                var org = ParseOrganizationCell(cell);
                if (org != null)
                {
                    organizations.Add(org);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error parsing organization cell");
            }
        }

        return organizations;
    }

    private OrganizationData? ParseOrganizationCell(HtmlNode cell)
    {
        // Extract SID from link
        var sid = ExtractSid(cell);
        if (string.IsNullOrEmpty(sid))
        {
            return null;
        }

        // Extract name
        var name = ExtractName(cell);
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        // Extract URLs
        var urlImage = ExtractImage(cell);
        var urlCorpo = ExtractUrl(cell);

        // Extract info values
        var infoValues = ExtractInfoValues(cell);

        return new OrganizationData
        {
            Sid = sid,
            Name = name,
            UrlImage = urlImage,
            UrlCorpo = urlCorpo,
            Archetype = infoValues.ElementAtOrDefault(0),
            Lang = infoValues.ElementAtOrDefault(1),
            Commitment = infoValues.ElementAtOrDefault(2),
            Recruiting = ParseBoolean(infoValues.ElementAtOrDefault(3)),
            Roleplay = ParseBoolean(infoValues.ElementAtOrDefault(4)),
            MembersCount = ParseMemberCount(infoValues.ElementAtOrDefault(5))
        };
    }

    private string? ExtractSid(HtmlNode cell)
    {
        var link = cell.SelectSingleNode(".//a[contains(@class, 'trans-03s')]");
        var href = link?.GetAttributeValue("href", "");
        if (string.IsNullOrEmpty(href))
        {
            // Try alternative: data attribute
            href = cell.GetAttributeValue("data-href", "");
        }

        if (string.IsNullOrEmpty(href))
        {
            return null;
        }

        // Extract SID from URL like /orgs/TEST
        var parts = href.Split('/');
        return parts.Length > 0 ? parts[^1] : null;
    }

    private string? ExtractName(HtmlNode cell)
    {
        // Try title attribute
        var nameNode = cell.SelectSingleNode(".//h1|././/h2|././/*[contains(@class, 'name')]");
        var name = nameNode?.InnerText?.Trim();

        if (string.IsNullOrEmpty(name))
        {
            // Try alt attribute on image
            var img = cell.SelectSingleNode(".//img");
            name = img?.GetAttributeValue("alt", "");
        }

        return name;
    }

    private string? ExtractImage(HtmlNode cell)
    {
        var img = cell.SelectSingleNode(".//img");
        var src = img?.GetAttributeValue("src", "");
        return !string.IsNullOrEmpty(src) ? src : null;
    }

    private string? ExtractUrl(HtmlNode cell)
    {
        var link = cell.SelectSingleNode(".//a[contains(@class, 'trans-03s')]");
        return link?.GetAttributeValue("href", "");
    }

    private List<string> ExtractInfoValues(HtmlNode cell)
    {
        var values = new List<string>();

        // Try info items
        var infoNodes = cell.SelectNodes(".//*[contains(@class, 'info-item')]//*[contains(@class, 'value')]");
        if (infoNodes != null)
        {
            values.AddRange(infoNodes.Select(n => n.InnerText?.Trim() ?? ""));
        }

        // Try alternative: stat values
        var statNodes = cell.SelectNodes(".//*[contains(@class, 'stat')]//*[contains(@class, 'value')]");
        if (statNodes != null)
        {
            values.AddRange(statNodes.Select(n => n.InnerText?.Trim() ?? ""));
        }

        return values;
    }

    private bool? ParseBoolean(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        return value.ToLowerInvariant() switch
        {
            "yes" => true,
            "true" => true,
            "1" => true,
            "no" => false,
            "false" => false,
            "0" => false,
            _ => null
        };
    }

    private int ParseMemberCount(string? value)
    {
        if (string.IsNullOrEmpty(value)) return 0;

        // Remove commas and spaces
        value = value.Replace(",", "").Replace(" ", "");

        if (int.TryParse(value, out var count))
        {
            return count;
        }

        return 0;
    }
}