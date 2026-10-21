using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Connections;
using Microsoft.Extensions.Logging;

namespace FerretSharp.UI.State;

/// <summary>
/// Logs the steps of connecting one connection (opening the session, the schema queries, the workspaces): a connect that
/// seems to hang shows in the log where it stands, and the durations of all steps follow when it has ended. Created and
/// disposed with the connection's scope, like <see cref="ClrActivityLog"/>.
/// </summary>
public sealed class ConnectActivityLog : IDisposable
{
    private readonly ActiveConnection _active;
    private readonly ILogger _logger;
    private readonly string _connection;
    private readonly Lock _lock = new();
    private string? _step;
    private IReadOnlyList<LoadStep>? _logged;

    /// <param name="connection">The connection's name, in every line.</param>
    public ConnectActivityLog(ActiveConnection active, ILogger logger, string connection)
    {
        _active = active;
        _logger = logger;
        _connection = connection;
        _logged = active.Steps;
        _active.StepChanged += OnStepChanged;
        _active.SessionOpenEnded += OnSessionOpenEnded;
    }

    /// <summary>On the thread pool: tells a hanging open from a UI thread that holds up the next step.</summary>
    private void OnSessionOpenEnded(SessionOpenOutcome outcome)
    {
        var blocking = outcome.Synchronous ? " (blocking the calling thread)" : "";
        if (outcome.Error is null)
        {
            _logger.LogInformation("{Connection}: session open after {Milliseconds} ms{Blocking}", _connection, (long)outcome.Took.TotalMilliseconds, blocking);
        }
        else
        {
            _logger.LogInformation("{Connection}: opening the session ended without one after {Milliseconds} ms{Blocking}: {Error}",
                _connection, (long)outcome.Took.TotalMilliseconds, blocking, outcome.Error);
        }
    }

    private void OnStepChanged()
    {
        var step = _active.Step;
        var steps = _active.Steps;
        lock (_lock)
        {
            if (step is not null)
            {
                if (step != _step)
                {
                    _step = step;
                    _logger.LogInformation("{Connection}: connecting – {Step}", _connection, step);
                }

                return;
            }

            _step = null;
            if (ReferenceEquals(steps, _logged))
            {
                return;
            }

            _logged = steps;
        }

        _logger.LogInformation("{Connection}: connect took {Total} ms – {Steps}", _connection,
            (long)steps.Sum(s => s.Duration.TotalMilliseconds), string.Join(", ", steps.Select(s => $"{s.Name} {(long)s.Duration.TotalMilliseconds} ms")));
    }

    public void Dispose()
    {
        _active.StepChanged -= OnStepChanged;
        _active.SessionOpenEnded -= OnSessionOpenEnded;
    }
}
