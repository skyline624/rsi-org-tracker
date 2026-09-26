using HtmlAgilityPack;

namespace Collector.Parsers;

/// <summary>
/// Text RSI serves HTML-encoded, some organization names twice ("Concepts &amp;amp;
/// Xenotech"): decoded until it no longer changes, so a name is stored the same way
/// however many times it was encoded, and decoding it again changes nothing.
/// </summary>
public static class HtmlText
{
    public static string Decode(string text)
    {
        for (var i = 0; i < 5; i++)
        {
            var decoded = HtmlEntity.DeEntitize(text);
            if (decoded == text) break;
            text = decoded;
        }
        return text;
    }
}
