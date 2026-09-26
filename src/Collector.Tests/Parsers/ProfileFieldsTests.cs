using Collector.Parsers;
using Collector.Tests.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Collector.Tests.Parsers;

/// <summary>
/// The other profile fields, read from RSI's current markup: labelled entries
/// (<c>span.label</c> + <c>.value</c>) and, for the display name, the first
/// unlabelled entry of the profile block. The end-to-end run on 2026-09-26 found
/// every enriched user stored with no display name, location or enlistment date.
/// </summary>
public class ProfileFieldsTests
{
    private readonly UserProfileHtmlParser _parser = new(NullLogger<UserProfileHtmlParser>.Instance);

    /// <summary>The fixture, with a display name that differs from the handle.</summary>
    private static string Profile()
    {
        var html = RsiFixtures.Text("profile-citizen.html");
        const string firstValue = "<strong class=\"value\">fixture-pilot</strong>";
        var i = html.IndexOf(firstValue, StringComparison.Ordinal);
        return html[..i] + "<strong class=\"value\">Fixture Pilot</strong>" + html[(i + firstValue.Length)..];
    }

    [Fact]
    public void DisplayName_IsTheProfilesFirstUnlabelledEntry()
    {
        var data = _parser.ParseProfile(Profile()).Data!;

        data.DisplayName.Should().Be("Fixture Pilot");
        data.Handle.Should().Be("fixture-pilot");
    }

    [Fact]
    public void Avatar_IsTheProfileThumbnail()
    {
        // As served: a relative path with nothing that says "avatar" (the front adds the host).
        var html = Profile().Replace(
            "https://robertsspaceindustries.com/media/fixture/avatar.jpg",
            "/media/h9j1q0duemtl3r/heap_infobox/5e35322a.jpg");

        _parser.ParseProfile(html).Data!.UrlImage.Should().Be("/media/h9j1q0duemtl3r/heap_infobox/5e35322a.jpg");
    }

    [Fact]
    public void Enlisted_IsTheEnlistedEntry()
    {
        _parser.ParseProfile(Profile()).Data!.Enlisted.Should().Be(new DateTime(2020, 1, 1));
    }

    [Fact]
    public void Bio_IsTheBioValue_WithoutItsLabel()
    {
        _parser.ParseProfile(Profile()).Data!.Bio.Should().Be("Fixture bio.");
    }

    [Fact]
    public void Location_IsTheLocationEntry_WithItsWhitespaceCollapsed()
    {
        var html = Profile().Replace(
            "<span class=\"label\">Fluency</span>",
            "<span class=\"label\">Location</span>\n<strong class=\"value\">\n   France,\n   Paris  </strong></p><p class=\"entry\"><span class=\"label\">Fluency</span>");

        _parser.ParseProfile(html).Data!.Location.Should().Be("France, Paris");
    }

    [Fact]
    public void MissingEntries_AreNull()
    {
        var html = Profile()
            .Replace("<span class=\"label\">Enlisted</span>", "<span class=\"label\">Other</span>")
            .Replace("<span class=\"label\">Bio</span>", "<span class=\"label\">Other</span>");

        var data = _parser.ParseProfile(html).Data!;

        data.Location.Should().BeNull();
        data.Enlisted.Should().BeNull();
        data.Bio.Should().BeNull();
    }
}
