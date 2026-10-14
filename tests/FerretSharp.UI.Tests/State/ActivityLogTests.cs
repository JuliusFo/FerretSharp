using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Connections;
using FerretSharp.UI.State;
using Microsoft.Extensions.Logging;

namespace FerretSharp.UI.Tests.State;

/// <summary>
/// The log lines of one connection (ADR 0016, fixes of 2026-10-14): each step of connecting once, the durations when it has
/// ended; each new state of the C# model once (the LINQ console the same way; without a linked project it never changes).
/// Nothing after the connection's scope is gone.
/// </summary>
public sealed class ActivityLogTests : IAsyncDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TestApp _app = new();
    private readonly ListLogger _logger = new();

    [Fact]
    public async Task Connecting_logs_each_step_and_then_the_durations()
    {
        var profile = TestApp.Profile("ERP Test");
        _app.Secrets.SetPassword(profile.Id, "pw");
        var scope = _app.Hub.Show(profile);
        using (new ConnectActivityLog(scope.Active, _logger, "ERP Test"))
        {
            await scope.Active.ConnectAsync(profile, Ct);
        }

        var steps = scope.Active.Steps.Select(s => s.Name).ToList();
        Assert.NotEmpty(steps);
        Assert.Equal(steps.Select(s => $"ERP Test: connecting – {s}").Append("summary"),
            _logger.Lines.Select(l => l.StartsWith("ERP Test: connect took ", StringComparison.Ordinal) ? "summary" : l));
        Assert.All(steps, step => Assert.Contains($"{step} ", _logger.Lines[^1], StringComparison.Ordinal));
    }

    [Fact]
    public async Task Nothing_is_logged_once_the_log_is_disposed()
    {
        var profile = TestApp.Profile("ERP Test");
        _app.Secrets.SetPassword(profile.Id, "pw");
        var scope = _app.Hub.Show(profile);
        new ConnectActivityLog(scope.Active, _logger, "ERP Test").Dispose();

        await scope.Active.ConnectAsync(profile, Ct);

        Assert.True(scope.Active.IsConnected);
        Assert.Empty(_logger.Lines);
    }

    [Fact]
    public async Task The_model_logs_each_new_state_once()
    {
        var scope = await _app.OpenAsync(TestApp.Profile("ERP Test")); // no C# project linked
        using (new ClrActivityLog(scope.Models, scope.Linq, _logger, "ERP Test"))
        {
            await scope.Models.LoadAsync();
            await scope.Models.LoadAsync(); // the same state again: no second line
        }

        await scope.Models.LoadAsync(); // after the scope: nothing

        Assert.Equal([$"ERP Test: C# model {ClrModelPhase.None}"], _logger.Lines);
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();

    /// <summary>The formatted lines, in order; written from any thread.</summary>
    private sealed class ListLogger : ILogger
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
}
