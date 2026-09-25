using System.Globalization;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Collector.Dtos;
using Microsoft.Extensions.Logging;

namespace Collector.Parsers;

/// <summary>One roster page: the visible members, and how many rows RSI masked.</summary>
public sealed record MemberPage(IReadOnlyList<MemberData> Visible, int RawRows, int RedactedRows, int HiddenRows);

/// <summary>
/// Parses the roster HTML returned by <c>orgs/getOrgMembers</c>. Every row is an
/// <c>li.member-item</c> whose <c>org-visibility-*</c> token tells what it shows:
/// V visible, R redacted (the member hides their memberships), H hidden affiliation.
/// R and H rows carry no handle: they are counted, never turned into members.
/// Fields are read from their semantic class tokens (nick, name, rank, stars,
/// rolelist); the <c>dataN</c> classes vary from page to page and are never used.
/// </summary>
public class MemberHtmlParser
{
    /// <summary>
    /// Recorded with each member collection. Version 1 was the heuristic parser whose
    /// ranks were the "Roles"/"Affiliate" overlay titles.
    /// </summary>
    public const int Version = 2;

    private static readonly Regex HandleShape = new(@"^[A-Za-z0-9_-]{1,50}$", RegexOptions.Compiled);
    private static readonly Regex StarsWidth = new(@"width:\s*(\d+(?:\.\d+)?)\s*%", RegexOptions.Compiled);

    private readonly ILogger<MemberHtmlParser> _logger;

    public MemberHtmlParser(ILogger<MemberHtmlParser> logger)
    {
        _logger = logger;
    }

    public MemberPage ParsePage(string html, string orgSid)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return new MemberPage([], 0, 0, 0);
        }

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var rows = doc.DocumentNode.Descendants("li").Where(li => li.HasClass("member-item")).ToList();
        var visible = new List<MemberData>(rows.Count);
        var redacted = 0;
        var hidden = 0;

        foreach (var row in rows)
        {
            switch (Visibility(row))
            {
                case 'R':
                    redacted++;
                    continue;
                case 'H':
                    hidden++;
                    continue;
            }

            var member = ParseVisibleRow(row, orgSid);
            if (member != null)
            {
                visible.Add(member);
            }
            else
            {
                _logger.LogWarning("Visible roster row without a readable handle in org {OrgSid}", orgSid);
            }
        }

        return new MemberPage(visible, rows.Count, redacted, hidden);
    }

    /// <summary>V, R or H; a row without a visibility token is treated as visible.</summary>
    private static char Visibility(HtmlNode row)
    {
        const string prefix = "org-visibility-";
        var token = row.GetClasses().FirstOrDefault(c => c.StartsWith(prefix, StringComparison.Ordinal));
        return token is { Length: > 15 } ? char.ToUpperInvariant(token[prefix.Length]) : 'V';
    }

    private static MemberData? ParseVisibleRow(HtmlNode row, string orgSid)
    {
        var handle = Handle(row);
        if (handle == null)
        {
            return null;
        }

        return new MemberData
        {
            OrgSid = orgSid,
            Handle = handle,
            DisplayName = Text(Descendant(row, "name")),
            Rank = Text(Descendant(row, "rank")),
            Stars = Stars(row),
            Roles = Roles(row),
            UrlImage = Descendant(row, "thumb")?.Descendants("img").FirstOrDefault()
                ?.GetAttributeValue("src", null),
        };
    }

    /// <summary>The nick element, else the /citizens/{handle} link.</summary>
    private static string? Handle(HtmlNode row)
    {
        var nick = Text(Descendant(row, "nick"));
        if (nick != null && HandleShape.IsMatch(nick))
        {
            return nick;
        }

        var href = row.Descendants("a").Select(a => a.GetAttributeValue("href", ""))
            .FirstOrDefault(h => h.Contains("/citizens/", StringComparison.Ordinal));
        var fromLink = href?[(href.LastIndexOf('/') + 1)..];
        return fromLink != null && HandleShape.IsMatch(fromLink) ? fromLink : null;
    }

    /// <summary>The stars bar is 20% wide per star: 0-100% maps to 0-5.</summary>
    private static int? Stars(HtmlNode row)
    {
        var style = Descendant(row, "stars")?.GetAttributeValue("style", null);
        var match = style == null ? null : StarsWidth.Match(style);
        if (match is not { Success: true })
        {
            return null;
        }

        var percent = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        return Math.Clamp((int)Math.Round(percent / 20, MidpointRounding.AwayFromZero), 0, 5);
    }

    private static string[]? Roles(HtmlNode row)
    {
        var roles = Descendant(row, "rolelist")?.Descendants("li")
            .Where(li => li.HasClass("role"))
            .Select(Text)
            .OfType<string>()
            .ToArray();
        return roles is { Length: > 0 } ? roles : null;
    }

    private static HtmlNode? Descendant(HtmlNode row, string classToken)
        => row.Descendants().FirstOrDefault(n => n.HasClass(classToken));

    private static string? Text(HtmlNode? node)
    {
        var text = node == null ? null : HtmlEntity.DeEntitize(node.InnerText).Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }
}
