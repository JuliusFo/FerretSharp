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
        var lines = _logger.Lines.Where(l => !IsSessionOpenLine(l)).ToList(); // from the thread pool, anywhere in between
        Assert.NotEmpty(steps);
        Assert.Equal(steps.Select(s => $"ERP Test: connecting – {s}").Append("summary"),
            lines.Select(l => l.StartsWith("ERP Test: connect took ", StringComparison.Ordinal) ? "summary" : l));
        Assert.All(steps, step => Assert.Contains($"{step} ", lines[^1], StringComparison.Ordinal));
    }

    /// <summary>After sleep (2026-10-21) the log could not tell a hanging open from a UI thread that held up the next step.</summary>
    [Fact]
    public async Task Connecting_logs_when_the_session_opened()
    {
        var profile = TestApp.Profile("ERP Test");
        _app.Secrets.SetPassword(profile.Id, "pw");
        var scope = _app.Hub.Show(profile);
        using (new ConnectActivityLog(scope.Active, _logger, "ERP Test"))
        {
            await scope.Active.ConnectAsync(profile, Ct);
            for (var waited = 0; !_logger.Lines.Any(IsSessionOpenLine); waited += 10)
            {
                Assert.True(waited < 5_000, "No line about the session.");
                await Task.Delay(10, Ct);
            }
        }

        Assert.Matches(@"^ERP Test: session open after \d+ ms", Assert.Single(_logger.Lines, IsSessionOpenLine));
    }

    private static bool IsSessionOpenLine(string line) =>
        line.StartsWith("ERP Test: session open after ", StringComparison.Ordinal)
        || line.StartsWith("ERP Test: opening the session ended", StringComparison.Ordinal);

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
}
