using System.Net;

namespace Collector.Parsers;

/// <summary>
/// Text RSI serves HTML-encoded, some organization names twice ("Concepts &amp;amp;
/// Xenotech"): decoded until it no longer changes, so a name is stored the same way
/// however many times it was encoded, and decoding it again changes nothing.
/// WebUtility.HtmlDecode, not HtmlAgilityPack's DeEntitize: the latter mangles
/// numeric entities above U+FFFF ("&#128640;" became "&####128640;") and malformed
/// ones, which never became stable.
/// </summary>
public static class HtmlText
{
    public static string Decode(string text)
    {
        for (var i = 0; i < 5; i++)
        {
            var decoded = WebUtility.HtmlDecode(text);
            if (decoded == text) break;
            text = decoded;
        }
        return text;
    }
}
