using System.Text.RegularExpressions;
using Collector.Parsers;
using Collector.Tests.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Collector.Tests.Parsers;

/// <summary>Roster pages as RSI serves them (anonymized captures).</summary>
public class MemberHtmlParserTests
{
    private readonly MemberHtmlParser _parser = new(NullLogger<MemberHtmlParser>.Instance);

    [Fact]
    public void EmptyHtml_HasNoRows()
    {
        var page = _parser.ParsePage("", "FIXTURE");

        page.Visible.Should().BeEmpty();
        page.RawRows.Should().Be(0);
    }

    [Fact]
    public void HiddenRows_AreCounted_ButDoNotBecomeMembers()
    {
        var page = _parser.ParsePage(RsiFixtures.MembersHtml("members-visible-hidden.json"), "FIXTURE");

        page.RawRows.Should().Be(32);
        page.HiddenRows.Should().Be(2);
        page.RedactedRows.Should().Be(0);
        page.Visible.Should().HaveCount(30);
    }

    [Fact]
    public void RedactedRows_AreCountedApartFromHiddenOnes()
    {
        var page = _parser.ParsePage(RsiFixtures.MembersHtml("members-redacted.json"), "FIXTURE");

        page.RawRows.Should().Be(32);
        page.RedactedRows.Should().Be(1);
        page.HiddenRows.Should().Be(2);
        page.Visible.Should().HaveCount(29);
    }

    [Fact]
    public void VisibleRow_FieldsComeFromTheirOwnElements()
    {
        var page = _parser.ParsePage(RsiFixtures.MembersHtml("members-visible-hidden.json"), "FIXTURE");

        var first = page.Visible[0];
        first.OrgSid.Should().Be("FIXTURE");
        first.Handle.Should().Be("pilot-001");
        first.DisplayName.Should().Be("Display 001");
        first.Rank.Should().Be("Cadet", "the 'Roles' / 'Affiliate' overlay title is not the rank");
        first.Stars.Should().Be(0);
        first.Roles.Should().BeNull();
        first.UrlImage.Should().Be("https://robertsspaceindustries.com/media/fixture/avatar-001.jpg");
        first.CitizenId.Should().BeNull("roster rows carry no citizen number");
        page.Visible.Should().OnlyContain(m => m.Rank == "Cadet");
    }

    [Fact]
    public void RankStarsAndRoles_OfSeniorMembers()
    {
        var page = _parser.ParsePage(RsiFixtures.MembersHtml("members-roles.json"), "FIXTURE");

        page.Visible.Should().HaveCount(2);
        var founder = page.Visible[0];
        founder.Rank.Should().Be("Admiral");
        founder.Stars.Should().Be(5);
        founder.Roles.Should().Equal("Role 001", "Role 002", "Role 003", "Role 004");
    }

    [Fact]
    public void FullyMaskedPage_HasRowsButNoMembers()
    {
        var page = _parser.ParsePage(RsiFixtures.MembersHtml("members-all-masked.json"), "FIXTURE");

        page.Visible.Should().BeEmpty();
        page.RawRows.Should().Be(5);
        (page.RedactedRows + page.HiddenRows).Should().Be(5);
    }

    [Fact]
    public void RenumberedDataClasses_DoNotChangeTheResult()
    {
        var html = RsiFixtures.MembersHtml("members-roles.json") + RsiFixtures.MembersHtml("members-redacted.json");
        var shuffled = Regex.Replace(html, @"\bdata(\d)\b", m => $"data{(int.Parse(m.Groups[1].Value) * 7 + 3) % 10}");

        var expected = _parser.ParsePage(html, "FIXTURE");
        var actual = _parser.ParsePage(shuffled, "FIXTURE");

        shuffled.Should().NotBe(html);
        actual.Should().BeEquivalentTo(expected);
    }
}
