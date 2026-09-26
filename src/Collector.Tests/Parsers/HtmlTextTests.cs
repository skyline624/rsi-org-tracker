using Collector.Parsers;
using FluentAssertions;
using Xunit;

namespace Collector.Tests.Parsers;

/// <summary>Decoding RSI's HTML-encoded text until it no longer changes.</summary>
public class HtmlTextTests
{
    [Theory]
    [InlineData("Steal &amp; Deal", "Steal & Deal")]
    [InlineData("Concepts &amp;amp; Xenotech", "Concepts & Xenotech")]
    [InlineData("Les H&eacute;raults", "Les Héraults")]
    // HtmlAgilityPack's DeEntitize turned this into "Rocket &####128640; Org".
    [InlineData("Rocket &#128640; Org", "Rocket 🚀 Org")]
    [InlineData("Rocket &#x1F680; Org", "Rocket 🚀 Org")]
    // Not an entity: left alone, and decoding again changes nothing.
    [InlineData("A &#; B", "A &#; B")]
    [InlineData("Salt & Pepper; Co", "Salt & Pepper; Co")]
    public void Decode_GivesTheText_AndIsStable(string encoded, string expected)
    {
        var decoded = HtmlText.Decode(encoded);

        decoded.Should().Be(expected);
        HtmlText.Decode(decoded).Should().Be(decoded);
    }
}
