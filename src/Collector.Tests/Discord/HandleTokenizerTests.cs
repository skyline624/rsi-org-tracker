using Collector.Discord;
using FluentAssertions;
using Xunit;

namespace Collector.Tests.Discord;

/// <summary>
/// Spec § 10.1 normalisation of Discord names into candidate RSI handles: bracketed segments
/// dropped, tokens split on anything outside [A-Za-z0-9_-], 3 to 60 characters, plus the whole
/// name without whitespace or emoji; distinct regardless of case.
/// </summary>
public class HandleTokenizerTests
{
    [Theory]
    [InlineData("[CORP] Pilote42")]
    [InlineData("(CORP) Pilote42")]
    [InlineData("{CORP} Pilote42")]
    [InlineData("«CORP» Pilote42")]
    [InlineData("Pilote42 [Officier]")]
    public void BracketedSegments_AreDropped(string value)
        => HandleTokenizer.Tokens(value).Should().Equal("Pilote42");

    [Fact]
    public void EveryKindOfBracket_IsDroppedInOneName()
        => HandleTokenizer.Tokens("[A] (afk) Pilote42 {x} «y»").Should().Equal("Pilote42");

    [Fact]
    public void AnUnclosedBracket_IsASeparator()
        => HandleTokenizer.Tokens("[CORP Pilote42").Should().Equal("CORP", "Pilote42", "[CORPPilote42");

    [Fact]
    public void Tokens_AreSplitOnEveryCharacterOutsideTheHandleAlphabet()
        => HandleTokenizer.Tokens("ace.pilot|sky/high:42")
            .Should().Equal("ace", "pilot", "sky", "high", "ace.pilot|sky/high:42");

    [Fact]
    public void UnderscoresAndHyphens_StayInsideAToken()
        => HandleTokenizer.Tokens("sky_high-42").Should().Equal("sky_high-42");

    [Fact]
    public void LettersOutsideAscii_SplitTokens()
        => HandleTokenizer.Tokens("Élodie").Should().Equal("lodie", "Élodie");

    [Theory]
    [InlineData("ab")]
    [InlineData("a b")]
    public void CandidatesShorterThanThree_AreDropped(string value)
        => HandleTokenizer.Tokens(value).Should().BeEmpty();

    [Fact]
    public void ShortWords_StillCountInTheWholeName()
        => HandleTokenizer.Tokens("ab cd").Should().Equal("abcd");

    [Fact]
    public void ThreeToSixtyCharacters_AreKept()
    {
        var sixty = new string('a', 60);

        HandleTokenizer.Tokens("abc").Should().Equal("abc");
        HandleTokenizer.Tokens(sixty).Should().Equal(sixty);
        HandleTokenizer.Tokens(new string('a', 61)).Should().BeEmpty();
        HandleTokenizer.Tokens("abc " + new string('b', 61)).Should().Equal("abc");
    }

    [Theory]
    [InlineData("Pilote 42")]
    [InlineData("\U0001F680 Pilote 42 \U0001F680")]
    [InlineData("Pilote\t42")]
    [InlineData("Pilote\u00A042")]
    public void TheWholeName_WithoutSpacesOrEmoji_IsACandidate(string value)
        => HandleTokenizer.Tokens(value).Should().Equal("Pilote", "Pilote42");

    [Theory]
    [InlineData("\U0001F469\U0001F3FD\u200D\U0001F680Ace99")]
    [InlineData("\U0001F1EB\U0001F1F7 Ace99")]
    [InlineData("\u202EAce99")]
    [InlineData("Ace99\u200D\uFE0F")]
    public void EmojiAndInvisibleCharacters_NeverReachACandidate(string value)
        => HandleTokenizer.Tokens(value).Should().Equal("Ace99");

    [Fact]
    public void AZeroWidthSpace_SplitsTokens_ButNotTheWholeName()
        => HandleTokenizer.Tokens("Pi\u200Blote").Should().Equal("lote", "Pilote");

    [Fact]
    public void ANameMadeOnlyOfEmoji_GivesNoCandidate()
        => HandleTokenizer.Tokens("\U0001F680\U0001F680\U0001F680").Should().BeEmpty();

    [Fact]
    public void Tokens_AreDistinctRegardlessOfCase_KeepingTheFirstSpelling()
        => HandleTokenizer.Tokens("Pilote42 PILOTE42 pilote42")
            .Should().Equal("Pilote42", "Pilote42PILOTE42pilote42");

    [Fact]
    public void Candidates_TakeTheNickThenTheGlobalNameThenTheUsername()
        => HandleTokenizer.Candidates("[CORP] Ace99", "Ace 99", "ace_99").Should().Equal("Ace99", "Ace", "ace_99");

    [Fact]
    public void Candidates_IgnoreCaseAcrossTheThreeNames()
        => HandleTokenizer.Candidates("Pilote42", "PILOTE42", "pilote42").Should().Equal("Pilote42");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoName_GivesNoToken(string? value)
        => HandleTokenizer.Tokens(value).Should().BeEmpty();

    [Fact]
    public void MissingNickAndGlobalName_LeaveTheUsername()
    {
        HandleTokenizer.Candidates(null, null, "ace99").Should().Equal("ace99");
        HandleTokenizer.Candidates(null, "   ", "ab").Should().BeEmpty();
    }
}
