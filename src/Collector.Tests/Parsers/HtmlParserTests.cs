using Collector.Parsers;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Collector.Tests.Parsers;

public class OrganizationHtmlParserTests
{
    private readonly OrganizationHtmlParser _parser;

    public OrganizationHtmlParserTests()
    {
        var logger = new Mock<ILogger<OrganizationHtmlParser>>();
        _parser = new OrganizationHtmlParser(logger.Object);
    }

    [Fact]
    public void ParseOrganizations_EmptyHtml_ReturnsEmptyList()
    {
        // Arrange
        var html = "<html><body></body></html>";

        // Act
        var result = _parser.ParseOrganizations(html);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public void ParseOrganizations_ValidHtml_ReturnsOrganizations()
    {
        // Arrange
        var html = @"
        <html>
        <body>
            <div class='org-cell'>
                <a href='/orgs/TEST' class='trans-03s'>
                    <img src='https://example.com/logo.png' alt='Test Organization' />
                </a>
                <div class='info-item'>
                    <span class='value'>Organization</span>
                </div>
                <div class='info-item'>
                    <span class='value'>English</span>
                </div>
                <div class='info-item'>
                    <span class='value'>Casual</span>
                </div>
                <div class='info-item'>
                    <span class='value'>Yes</span>
                </div>
                <div class='info-item'>
                    <span class='value'>No</span>
                </div>
                <div class='info-item'>
                    <span class='value'>1,234</span>
                </div>
            </div>
        </body>
        </html>";

        // Act
        var result = _parser.ParseOrganizations(html);

        // Assert
        result.Should().NotBeEmpty();
        result.Should().HaveCount(1);
        result[0].Sid.Should().Be("TEST");
    }
}