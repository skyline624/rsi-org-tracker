using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Collector.Tests.TestSupport;

/// <summary>Keeps every formatted log line so tests can assert on what was reported.</summary>
public sealed class ListLogger<T> : ILogger<T>
{
    public ConcurrentQueue<string> Messages { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
        => Messages.Enqueue(formatter(state, exception));
}
