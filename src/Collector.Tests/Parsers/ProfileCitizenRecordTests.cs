using Collector.Parsers;
using Collector.Tests.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Collector.Tests.Parsers;

/// <summary>The citizen number is read from the "UEE Citizen Record" entry only.</summary>
public class ProfileCitizenRecordTests
{
    private readonly UserProfileHtmlParser _parser = new(NullLogger<UserProfileHtmlParser>.Instance);

    [Fact]
    public void RealProfile_CitizenNumberComesFromTheRecord()
    {
        var result = _parser.ParseProfile(RsiFixtures.Text("profile-citizen.html"));

        result.Outcome.Should().Be(ProfileParseOutcome.Success);
        result.Data!.CitizenId.Should().Be(100001);
        result.Data.Handle.Should().Be("fixture-pilot");
    }

    [Fact]
    public void ANumberEarlierInThePage_IsNotMistakenForTheCitizenNumber()
    {
        var html = RsiFixtures.Text("profile-citizen.html").Replace(
            "<p class=\"entry citizen-record\">",
            "<p class=\"entry\">Squadron #42 </p><p class=\"entry citizen-record\">");

        _parser.ParseProfile(html).Data!.CitizenId.Should().Be(100001);
    }

    [Fact]
    public void RecordNa_IsNoCitizenNumber()
    {
        var result = _parser.ParseProfile(RsiFixtures.Text("profile-no-citizen-record.html"));

        result.Outcome.Should().Be(ProfileParseOutcome.NoCitizenNumber);
        result.Data.Should().BeNull();
    }
}
