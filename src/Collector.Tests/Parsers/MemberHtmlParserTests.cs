using Collector.Parsers;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Collector.Tests.Parsers;

public class MemberHtmlParserTests
{
    private readonly MemberHtmlParser _parser;

    public MemberHtmlParserTests()
    {
        var logger = new Mock<ILogger<MemberHtmlParser>>();
        _parser = new MemberHtmlParser(logger.Object);
    }

    [Fact]
    public void ParseMembers_EmptyHtml_ReturnsEmptyList()
    {
        // Arrange
        var html = "<html><body></body></html>";

        // Act
        var result = _parser.ParseMembers(html, "TEST");

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public void ParseMembers_ValidHtml_ReturnsMembers()
    {
        // Arrange
        var html = @"
        <html>
        <body>
            <table>
                <tr class='member-item'>
                    <td><a href='/citizens/TestHandle'>TestHandle</a></td>
                    <td class='rank'>Member</td>
                    <td><img src='https://example.com/avatar.png' /></td>
                </tr>
            </table>
        </body>
        </html>";

        // Act
        var result = _parser.ParseMembers(html, "TEST");

        // Assert
        result.Should().NotBeEmpty();
        result.Should().HaveCount(1);
        result[0].OrgSid.Should().Be("TEST");
        result[0].Handle.Should().Be("TestHandle");
        result[0].Rank.Should().Be("Member");
    }
}