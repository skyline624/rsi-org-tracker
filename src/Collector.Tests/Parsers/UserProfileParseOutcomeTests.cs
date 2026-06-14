using Collector.Parsers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Collector.Tests.Parsers;

/// <summary>
/// Covers <see cref="UserProfileHtmlParser.ParseProfile"/>'s three-way outcome that
/// drives the Phase 4 worker: a real profile (Success), a live profile with no UEE
/// Citizen Record yet ("n/a" → NoCitizenNumber, re-checkable later), and an
/// unparseable page (genuine failure that counts towards the retry cap).
/// </summary>
public sealed class UserProfileParseOutcomeTests
{
    private readonly UserProfileHtmlParser _parser = new(NullLogger<UserProfileHtmlParser>.Instance);

    [Fact]
    public void Profile_WithCitizenRecordNumber_IsSuccess()
    {
        const string html = """
            <html><body>
              <a href="/citizens/TestHandle">TestHandle</a>
              <div data-citizen-id="123456"></div>
              <p><span class="label">UEE Citizen Record</span><strong class="value">#123456</strong></p>
            </body></html>
            """;

        var result = _parser.ParseProfile(html);

        result.Outcome.Should().Be(ProfileParseOutcome.Success);
        result.Data.Should().NotBeNull();
        result.Data!.CitizenId.Should().Be(123456);
        result.Data.Handle.Should().Be("TestHandle");
    }

    [Fact]
    public void LiveProfile_WithCitizenRecordNa_IsNoCitizenNumber()
    {
        // Real profile page (has the "UEE Citizen Record" label + a citizens link)
        // but the record value is "n/a" — no number to extract. Must be deferrable,
        // NOT counted as a hard parse failure.
        const string html = """
            <html><body>
              <a href="/citizens/Nozzy">Nozzy</a>
              <p><span class="label">UEE Citizen Record</span><strong class="value">n/a</strong></p>
            </body></html>
            """;

        var result = _parser.ParseProfile(html);

        result.Outcome.Should().Be(ProfileParseOutcome.NoCitizenNumber);
        result.Data.Should().BeNull();
    }

    [Fact]
    public void NonProfilePage_IsUnparseable()
    {
        // No citizens link, no record label — a challenge/error page we can't read.
        const string html = "<html><body><h1>Oops</h1></body></html>";

        var result = _parser.ParseProfile(html);

        result.Outcome.Should().Be(ProfileParseOutcome.Unparseable);
        result.Data.Should().BeNull();
    }

    [Fact]
    public void RecordZero_IsNotTreatedAsSuccess()
    {
        // "#000" must not be mistaken for a real citizen_id (would create a bogus
        // CitizenId=0 user). With a record label present it's a number-less profile.
        const string html = """
            <html><body>
              <a href="/citizens/ZeroGuy">ZeroGuy</a>
              <p><span class="label">UEE Citizen Record</span><strong class="value">#000</strong></p>
            </body></html>
            """;

        var result = _parser.ParseProfile(html);

        result.Outcome.Should().NotBe(ProfileParseOutcome.Success);
        result.Data.Should().BeNull();
    }
}
