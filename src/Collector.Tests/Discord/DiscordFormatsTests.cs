using System.Globalization;
using Collector.Discord;
using FluentAssertions;
using Xunit;

namespace Collector.Tests.Discord;

/// <summary>Snowflake validation and the ISO dates stored in event values.</summary>
public sealed class DiscordFormatsTests
{
    [Theory]
    [InlineData("12345678901234567", true)]           // 17 digits
    [InlineData("12345678901234567890", true)]        // 20 digits
    [InlineData("1234567890123456", false)]           // 16
    [InlineData("123456789012345678901", false)]      // 21
    [InlineData("12345678901234567a", false)]
    [InlineData("-1234567890123456", false)]
    [InlineData("12345678901234567\n", false)]        // "$" in a regex would accept the newline
    [InlineData("١٢٣٤٥٦٧٨٩٠١٢٣٤٥٦٧", false)]         // Arabic-Indic digits: "\d" would accept them
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Snowflake_IsSeventeenToTwentyAsciiDigits(string? value, bool valid)
    {
        DiscordSnowflake.IsValid(value).Should().Be(valid);
    }

    [Fact]
    public void Iso_IsUtcToTheSecond_WhateverTheCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("th-TH"); // Buddhist calendar: year 2569
        try
        {
            DiscordFormats.Iso(new DateTime(2026, 9, 30, 12, 0, 5, 999, DateTimeKind.Utc))
                .Should().Be("2026-09-30T12:00:05Z");
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
