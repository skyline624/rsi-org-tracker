namespace Collector.Exceptions;

/// <summary>
/// Exception thrown when the RSI API returns an error.
/// </summary>
public class RsiApiException : Exception
{
    public string? ErrorCode { get; }
    public int? StatusCode { get; }

    public RsiApiException(string message) : base(message) { }
    public RsiApiException(string message, Exception innerException)
        : base(message, innerException) { }

    public RsiApiException(string message, string? errorCode, int? statusCode)
        : base(message)
    {
        ErrorCode = errorCode;
        StatusCode = statusCode;
    }
}