namespace Collector.Exceptions;

/// <summary>
/// Exception thrown when the RSI API returns a throttling response.
/// </summary>
public class RsiThrottledException : Exception
{
    public RsiThrottledException() : base("RSI API is throttled") { }
    public RsiThrottledException(string message) : base(message) { }
    public RsiThrottledException(string message, Exception innerException)
        : base(message, innerException) { }
}