using HtmlAgilityPack;
using Collector.Dtos;
using Microsoft.Extensions.Logging;

namespace Collector.Parsers;

/// <summary>
/// Parses the full RSI organization page HTML to extract extended content:
/// description, history, manifesto, charter, focus areas.
/// </summary>
public class OrgPageHtmlParser
{
    private readonly ILogger<OrgPageHtmlParser> _logger;

    public OrgPageHtmlParser(ILogger<OrgPageHtmlParser> logger)
    {
        _logger = logger;
    }

    public OrgPageData? Parse(string html, string sid)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        try
        {
            return new OrgPageData
            {
                Sid = sid,
                Description = ParseDescription(doc),
                History     = ParseTab(doc, "tab-history"),
                Manifesto   = ParseTab(doc, "tab-manifesto"),
                Charter     = ParseTab(doc, "tab-charter"),
                FocusPrimaryName    = ParseFocusName(doc, "primary"),
                FocusPrimaryImage   = ParseFocusImage(doc, "primary"),
                FocusSecondaryName  = ParseFocusName(doc, "secondary"),
                FocusSecondaryImage = ParseFocusImage(doc, "secondary"),
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parsing org page for {Sid}", sid);
            return null;
        }
    }

    /// <summary>
    /// Description/about: inside div.content.join-us > div.body.markitup-text
    /// </summary>
    private static string? ParseDescription(HtmlDocument doc)
    {
        var node = doc.DocumentNode.SelectSingleNode(
            "//div[contains(@class,'join-us')]//div[contains(@class,'markitup-text')]");
        return CleanText(node);
    }

    /// <summary>
    /// History / Manifesto / Charter: div#tab-{name} > div.markitup-text
    /// </summary>
    private static string? ParseTab(HtmlDocument doc, string tabId)
    {
        var node = doc.DocumentNode.SelectSingleNode(
            $"//div[@id='{tabId}']//div[contains(@class,'markitup-text')]");
        return CleanText(node);
    }

    private static string? ParseFocusName(HtmlDocument doc, string cssClass)
    {
        var img = doc.DocumentNode.SelectSingleNode(
            $"//ul[contains(@class,'focus')]//li[contains(@class,'{cssClass}')]//img");
        var alt = img?.GetAttributeValue("alt", null);
        return string.IsNullOrWhiteSpace(alt) ? null : alt.Trim();
    }

    private static string? ParseFocusImage(HtmlDocument doc, string cssClass)
    {
        var img = doc.DocumentNode.SelectSingleNode(
            $"//ul[contains(@class,'focus')]//li[contains(@class,'{cssClass}')]//img");
        var src = img?.GetAttributeValue("src", null);
        return string.IsNullOrWhiteSpace(src) ? null : src.Trim();
    }

    private static string? CleanText(HtmlNode? node)
    {
        if (node == null) return null;
        // Decode HTML entities, collapse whitespace
        var text = HtmlEntity.DeEntitize(node.InnerText).Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
}
