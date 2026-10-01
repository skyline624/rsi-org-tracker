namespace Collector.Discord;

/// <summary>
/// Discord ids (snowflakes) are 64-bit integers carried as decimal text end to end:
/// a JavaScript number loses their last digits, and tracker.db stores them as TEXT.
/// </summary>
public static class DiscordSnowflake
{
    /// <summary>
    /// True for 17 to 20 ASCII digits (<c>^[0-9]{17,20}$</c>). Checked by hand rather than by
    /// regex: <c>$</c> also matches before a trailing newline and <c>\d</c> accepts non-ASCII digits.
    /// </summary>
    public static bool IsValid(string? s) => s is { Length: >= 17 and <= 20 } && s.All(char.IsAsciiDigit);
}
