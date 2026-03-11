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
        var text = doc.DocumentNode.InnerText;
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
            var recordText = recordNode.InnerText;
            var match = System.Text.RegularExpressions.Regex.Match(recordText, @"#?(\d+)");
            if (match.Success && int.TryParse(match.Groups[1].Value, out citizenId))
            {
                return citizenId;
            }
        }

        return null;
    }

    private string? ExtractHandle(HtmlDocument doc)
    {
        // Try profile handle display
        var handleNode = doc.DocumentNode.SelectSingleNode("//*[@class='handle']|//*[@class='profile-handle']|//*[contains(@class, 'citizen-handle')]");
        var handle = handleNode?.InnerText?.Trim();

        if (string.IsNullOrEmpty(handle))
        {
            // Try from page title or heading
            handleNode = doc.DocumentNode.SelectSingleNode("//h1|//h2[contains(@class, 'name')]");
            handle = handleNode?.InnerText?.Trim();
        }

        if (string.IsNullOrEmpty(handle))
        {
            // Try from URL-based extraction
            handleNode = doc.DocumentNode.SelectSingleNode("//a[contains(@href, '/citizens/')]");
            var href = handleNode?.GetAttributeValue("href", "");
            if (!string.IsNullOrEmpty(href))
            {
                var parts = href.Split('/');
                handle = parts[^1];
            }
        }

        return handle;
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

        var text = enlistedNode.InnerText;

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