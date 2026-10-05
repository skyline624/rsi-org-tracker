using Collector.Discord;
using FluentAssertions;
using Xunit;

namespace Collector.Tests.Discord;

/// <summary>
/// Corpo tags in Discord names: the content of a bracketed segment, or a segment next to a
/// "|", shaped like an RSI SID (2 to 10 of [A-Za-z0-9_-]). Candidates are upper-cased like
/// stored SIDs; whether an organization really has that SID is checked against the database.
/// </summary>
public class CorpTagExtractorTests
{
    [Theory]
    [InlineData("[ABC] Pilote42")]
    [InlineData("(abc) Pilote42")]
    [InlineData("{ABC} Pilote42")]
    [InlineData("«ABC» Pilote42")]
    [InlineData("【ABC】Pilote42")]
    [InlineData("Pilote42 [ABC]")]
    [InlineData("[ ABC ] Pilote42")]
    public void ABracketedSidShapedSegment_IsATag(string value)
        => CorpTagExtractor.Candidates(value).Should().Equal("ABC");

    [Theory]
    [InlineData("ABC | Pilote42")]
    [InlineData("ABC|Pilote42")]
    public void TheSegmentsAroundABar_AreCandidates(string value)
        => CorpTagExtractor.Candidates(value).Should().Equal("ABC", "PILOTE42");

    [Fact]
    public void OnlyTheFirstAndLastSegmentsOfABarredName_AreCandidates()
        => CorpTagExtractor.Candidates("ABC | Pilote | XYZ").Should().Equal("ABC", "XYZ");

    [Theory]
    [InlineData("[ABC FR] Pilote42")]      // a space inside
    [InlineData("[A] Pilote42")]           // shorter than two
    [InlineData("[ABCDEFGHIJK] Pilote42")] // longer than ten
    [InlineData("[ÉCLAIR] Pilote42")]      // outside the SID alphabet
    [InlineData("Pilote42")]               // no bracket, no bar
    [InlineData("   ")]
    [InlineData(null)]
    public void ANameWithoutSidShapedSegment_HasNoCandidate(string? value)
        => CorpTagExtractor.Candidates(value).Should().BeEmpty();

    [Fact]
    public void AnUnshapedSideOfABar_IsLeftOut()
        => CorpTagExtractor.Candidates("Pilote | Pilote du dimanche").Should().Equal("PILOTE");

    [Fact]
    public void HyphensAndUnderscores_StayInATag()
        => CorpTagExtractor.Candidates("[ABC-FR] Pilote").Should().Equal("ABC-FR");

    [Fact]
    public void SeveralTags_AreKeptInOrder_WithoutDuplicates()
        => CorpTagExtractor.Candidates("[ABC][xyz] Pilote [abc]").Should().Equal("ABC", "XYZ");

    [Fact]
    public void AMemberCandidates_JoinTheNickAndTheGlobalName()
        => CorpTagExtractor.Candidates("[ABC] Pilote", "Pilote [XYZ] | abc").Should().Equal("ABC", "XYZ");
}
