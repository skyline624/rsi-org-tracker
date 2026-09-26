using System.Globalization;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Collector.Dtos;
using Microsoft.Extensions.Logging;

namespace Collector.Parsers;

/// <summary>Why a profile parse did not yield usable <see cref="UserProfileData"/>.</summary>
public enum ProfileParseOutcome
{
    /// <summary>Citizen_id and handle extracted.</summary>
    Success,
    /// <summary>Live profile, but it carries no UEE Citizen Record ("n/a"). Re-checkable later.</summary>
    NoCitizenNumber,
    /// <summary>Page could not be parsed as a profile at all. Genuine failure.</summary>
    Unparseable,
}

public record ProfileParseResult(UserProfileData? Data, ProfileParseOutcome Outcome);

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
    /// Parses user profile HTML into UserProfileData, or null when no usable
    /// citizen_id could be extracted. Kept for callers/tests that only care about
    /// the data; prefer <see cref="ParseProfile"/> when the failure reason matters.
    /// </summary>
    public UserProfileData? ParseUserProfile(string html) => ParseProfile(html).Data;

    /// <summary>
    /// Parses user profile HTML and reports WHY it failed when it does. This lets the
    /// enrichment worker tell a live profile that simply has no UEE Citizen Record
    /// ("n/a" — may gain one later, so worth re-checking) apart from a page it could
    /// not parse at all (genuine failure, counts towards the retry cap).
    /// </summary>
    public ProfileParseResult ParseProfile(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var citizenId = ExtractCitizenId(doc);
        var handle = ExtractHandle(doc);

        // A real citizen record number is strictly positive. "#000"/"n/a" profiles
        // (Star Citizen account holders who never completed enlistment) have none.
        if (citizenId is > 0 && !string.IsNullOrEmpty(handle))
        {
            return new ProfileParseResult(new UserProfileData
            {
                CitizenId = citizenId.Value,
                Handle = handle,
                DisplayName = ExtractDisplayName(doc),
                UrlImage = ExtractAvatarUrl(doc),
                Bio = ExtractBio(doc),
                Location = ExtractLocation(doc),
                Enlisted = ExtractEnlistedDate(doc)
            }, ProfileParseOutcome.Success);
        }

        // No usable number. Distinguish a real (but number-less) profile from a page
        // we simply couldn't parse: a genuine RSI profile always renders the
        // "UEE Citizen Record" label, even when its value is "n/a".
        var hasRecordLabel = doc.DocumentNode
            .SelectSingleNode("//*[contains(text(), 'UEE Citizen Record')]") != null;
        if (hasRecordLabel && !string.IsNullOrEmpty(handle))
        {
            _logger.LogDebug("Profile for handle has no UEE Citizen Record (n/a)");
            return new ProfileParseResult(null, ProfileParseOutcome.NoCitizenNumber);
        }

        _logger.LogWarning("Could not parse citizen_id/handle from profile HTML");
        return new ProfileParseResult(null, ProfileParseOutcome.Unparseable);
    }

    /// <summary>
    /// The value of the "UEE Citizen Record" entry ("#390065"), and nothing else:
    /// the page carries other '#' values earlier on (CSS colours, org texts).
    /// </summary>
    private static int? ExtractCitizenId(HtmlDocument doc)
    {
        var label = doc.DocumentNode.Descendants()
            .FirstOrDefault(n => n.HasClass("label")
                && HtmlEntity.DeEntitize(n.InnerText).Trim() == "UEE Citizen Record");
        var value = label?.ParentNode.Descendants("strong").FirstOrDefault(n => n.HasClass("value"));
        if (value == null)
        {
            return null;
        }

        var match = CitizenNumber.Match(HtmlEntity.DeEntitize(value.InnerText).Trim());
        return match.Success && int.TryParse(match.Groups[1].Value, out var citizenId) ? citizenId : null;
    }

    // RSI handles are URL-safe: alphanumerics, underscore, dash. Anything else
    // means we picked up a label ("CITIZEN DOSSIER", "UEE Citizen Record") by
    // mistake — better to return nothing than poison the users row.
    private static readonly Regex HandleShape = new(@"^[A-Za-z0-9_-]{3,50}$", RegexOptions.Compiled);
    private static readonly Regex CitizenNumber = new(@"^#?(\d+)$", RegexOptions.Compiled);

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

    // RSI's profile markup: labelled entries such as
    //   <p class="entry"><span class="label">Enlisted</span><strong class="value">Feb 14, 2014</strong></p>
    // (the bio is a div.entry with a div.value), and a "profile" block holding the
    // avatar thumbnail and, as its first unlabelled entry, the display name.
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    private static string HasClass(string name) =>
        $"contains(concat(' ', normalize-space(@class), ' '), ' {name} ')";

    private static string? Clean(HtmlNode? node)
    {
        if (node == null) return null;
        var text = Whitespace.Replace(HtmlEntity.DeEntitize(node.InnerText), " ").Trim();
        return text.Length == 0 ? null : text;
    }

    private static HtmlNode? ProfileBlock(HtmlDocument doc) =>
        doc.DocumentNode.SelectSingleNode($"//div[{HasClass("profile")}]");

    /// <summary>The value of the entry labelled <paramref name="label"/>, whitespace collapsed.</summary>
    private static string? EntryValue(HtmlDocument doc, string label) =>
        Clean(doc.DocumentNode.SelectSingleNode(
            $"//*[{HasClass("entry")}][*[{HasClass("label")} and normalize-space(.) = '{label}']]/*[{HasClass("value")}]"));

    private static string? ExtractDisplayName(HtmlDocument doc) =>
        Clean(ProfileBlock(doc)?.SelectSingleNode(
            $".//*[{HasClass("entry")}][not(*[{HasClass("label")}])]/strong[{HasClass("value")}]"));

    /// <summary>The src as served, usually a path relative to robertsspaceindustries.com.</summary>
    private static string? ExtractAvatarUrl(HtmlDocument doc)
    {
        var src = ProfileBlock(doc)?.SelectSingleNode($".//*[{HasClass("thumb")}]//img")?.GetAttributeValue("src", null);
        return string.IsNullOrWhiteSpace(src) ? null : src.Trim();
    }

    private static string? ExtractBio(HtmlDocument doc) => EntryValue(doc, "Bio");

    // RSI puts the region's comma on its own line ("United States\n , New Jersey").
    private static readonly Regex SpaceBeforeComma = new(@"\s+,", RegexOptions.Compiled);

    private static string? ExtractLocation(HtmlDocument doc) =>
        EntryValue(doc, "Location") is { } location ? SpaceBeforeComma.Replace(location, ",") : null;

    private static DateTime? ExtractEnlistedDate(HtmlDocument doc) =>
        DateTime.TryParseExact(EntryValue(doc, "Enlisted"), "MMM d, yyyy", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var date)
            ? DateTime.SpecifyKind(date, DateTimeKind.Utc)
            : null;
}