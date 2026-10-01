namespace Collector.Discord;

/// <summary>
/// tracker.db stayed locked by another connection (the collector) after one retry: the
/// sync is not fully written and the caller answers 503 with Retry-After. The transactions
/// already committed stay valid; the next sync completes the rest.
/// </summary>
public sealed class DiscordStoreBusyException : Exception
{
    public DiscordStoreBusyException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
