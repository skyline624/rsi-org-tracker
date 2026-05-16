using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Collector.Dtos;
using Microsoft.Extensions.Logging;

namespace Collector.Parsers;

/// <summary>
/// Parses user profile HTML from RSI citizen pages.
/// </summary>
public class UserProfileHtmlParser
{
    private readonly ILogger<UserProfileHtmlParser> _logger;

    public UserProfileHtmlParser(ILogger<UserProfileHtmlParser> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Parses user profile HTML into UserProfileData.
    /// </summary>
    public UserProfileData? ParseUserProfile(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        // Extract citizen_id
        var citizenId = ExtractCitizenId(doc);
        if (!citizenId.HasValue)
        {
            _logger.LogWarning("Could not extract citizen_id from profile HTML");
            return null;
        }

        // Extract handle
        var handle = ExtractHandle(doc);
        if (string.IsNullOrEmpty(handle))
        {
            _logger.LogWarning("Could not extract handle from profile HTML");
            return null;
        }

        return new UserProfileData
        {
            CitizenId = citizenId.Value,
            Handle = handle,
            DisplayName = ExtractDisplayName(doc),
            UrlImage = ExtractAvatarUrl(doc),
            Bio = ExtractBio(doc),
            Location = ExtractLocation(doc),
            Enlisted = ExtractEnlistedDate(doc)
        };
    }

    private int? ExtractCitizenId(HtmlDocument doc)
    {
        // Try data attribute
        var citizenIdNode = doc.DocumentNode.SelectSingleNode("//*[@data-citizen-id]");
        var citizenIdStr = citizenIdNode?.GetAttributeValue("data-citizen-id", "");

        if (!string.IsNullOrEmpty(citizenIdStr) && int.TryParse(citizenIdStr, out var citizenId))
        {
            return citizenId;
        }

        // Try text content like "#123456"
        var text = doc.DocumentNode.InnerText ?? string.Empty;
        var hashIndex = text.IndexOf('#');
        if (hashIndex >= 0)
        {
            var numberPart = text.Substring(hashIndex + 1).Split(' ')[0];
            if (int.TryParse(numberPart, out citizenId))
            {
                return citizenId;
            }
        }

        // Try citizen record number
        var recordNode = doc.DocumentNode.SelectSingleNode("//*[contains(text(), 'UEE Citizen Record')]");
        if (recordNode != null)
        {
            var recordText = recordNode.InnerText ?? string.Empty;
            var match = System.Text.RegularExpressions.Regex.Match(recordText, @"#?(\d+)");
            if (match.Success && int.TryParse(match.Groups[1].Value, out citizenId))
            {
                return citizenId;
            }
        }

        return null;
    }

    // RSI handles are URL-safe: alphanumerics, underscore, dash. Anything else
    // means we picked up a label ("CITIZEN DOSSIER", "UEE Citizen Record") by
    // mistake — better to return nothing than poison the users row.
    private static readonly Regex HandleShape = new(@"^[A-Za-z0-9_-]{3,50}$", RegexOptions.Compiled);

    private string? ExtractHandle(HtmlDocument doc)
    {
        // 1. URL-based — most reliable. RSI pages always link to themselves via
        //    /citizens/{handle}; the URL segment IS the handle by definition.
        foreach (var link in doc.DocumentNode.SelectNodes("//a[contains(@href, '/citizens/')]") ?? Enumerable.Empty<HtmlNode>())
        {
            var href = link.GetAttributeValue("href", "");
            var segment = href.TrimEnd('/').Split('/').LastOrDefault();
            if (HandleShape.IsMatch(segment ?? string.Empty)) return segment;
        }

        // 2. Specific RSI class selectors (kept as a defensive fallback).
        var handleNode = doc.DocumentNode.SelectSingleNode(
            "//*[@class='handle']|//*[@class='profile-handle']|//*[contains(@class, 'citizen-handle')]");
        var handle = handleNode?.InnerText?.Trim();
        if (handle is not null && HandleShape.IsMatch(handle)) return handle;

        // 3. <title> usually starts with the handle: "Abrams7K | Abrams7K - Liberastra | ...".
        var title = doc.DocumentNode.SelectSingleNode("//title")?.InnerText?.Trim();
        if (!string.IsNullOrEmpty(title))
        {
            var first = title.Split('|')[0].Trim();
            if (HandleShape.IsMatch(first)) return first;
        }

        // NOTE: we DO NOT fall back to //h1. RSI uses <h1>CITIZEN DOSSIER</h1>
        // as a section header on profile pages, and matching it poisoned ~78k
        // user rows in the past. Returning null here forces UserCollector to
        // skip the entry rather than write garbage.
        return null;
    }

    private string? ExtractDisplayName(HtmlDocument doc)
    {
        var nameNode = doc.DocumentNode.SelectSingleNode("//*[@class='name']|//*[contains(@class, 'display-name')]|//*[contains(@class, 'profile-name')]");
        return nameNode?.InnerText?.Trim();
    }

    private string? ExtractAvatarUrl(HtmlDocument doc)
    {
        var img = doc.DocumentNode.SelectSingleNode("//img[contains(@class, 'avatar')]|//img[contains(@class, 'profile-image')]|//img[contains(@src, 'avatar')]");
        return img?.GetAttributeValue("src", "");
    }

    private string? ExtractBio(HtmlDocument doc)
    {
        var bioNode = doc.DocumentNode.SelectSingleNode("//*[@class='bio']|//*[contains(@class, 'biography')]|//*[contains(@class, 'about')]");
        return bioNode?.InnerText?.Trim();
    }

    private string? ExtractLocation(HtmlDocument doc)
    {
        var locationNode = doc.DocumentNode.SelectSingleNode("//*[@class='location']|//*[contains(@class, 'region')]|//*[contains(@class, 'country')]");
        return locationNode?.InnerText?.Trim();
    }

    private DateTime? ExtractEnlistedDate(HtmlDocument doc)
    {
        var enlistedNode = doc.DocumentNode.SelectSingleNode("//*[contains(text(), 'Enlisted')]|//*[contains(@class, 'enlisted')]|//*[contains(@class, 'member-since')]");

        if (enlistedNode == null)
        {
            // Try to find any date-like content
            enlistedNode = doc.DocumentNode.SelectSingleNode("//*[contains(text(), 'Enlisted')]/..");
        }

        if (enlistedNode == null)
        {
            return null;
        }

        var text = enlistedNode.InnerText ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        // Try to parse date like "Enlisted: Jan 15, 2020" or similar
        var patterns = new[]
        {
            @"Enlisted[:\s]+(\w+\s+\d{1,2},?\s+\d{4})",
            @"(\d{4}-\d{2}-\d{2})",
            @"(\w+\s+\d{1,2},?\s+\d{4})"
        };

        foreach (var pattern in patterns)
        {
            var match = System.Text.RegularExpressions.Regex.Match(text, pattern);
            if (match.Success)
            {
                var dateStr = match.Groups[1].Value;
                if (DateTime.TryParse(dateStr, out var date))
                {
                    return date;
                }
            }
        }

        return null;
    }
}