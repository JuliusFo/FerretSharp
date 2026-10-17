using Microsoft.Extensions.Logging;

namespace FerretSharp.UI.Tests;

/// <summary>A logger that keeps the formatted lines, in order; written from any thread.</summary>
internal sealed class ListLogger : ILogger
{
    private readonly Lock _lock = new();
    private readonly List<string> _lines = [];

    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_lock)
            {
                return [.. _lines];
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_lock)
        {
            _lines.Add(formatter(state, exception));
        }
    }
}
