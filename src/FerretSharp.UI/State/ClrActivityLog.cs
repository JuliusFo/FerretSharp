using FerretSharp.Core.ClrModel;
using Microsoft.Extensions.Logging;

namespace FerretSharp.UI.State;

/// <summary>
/// Logs the steps of loading the C# model and starting the LINQ console of one open connection, so the times of UI
/// stalls in the log can be matched with what happened then (ADR 0016). Created and disposed with the connection's scope.
/// </summary>
public sealed class ClrActivityLog : IDisposable
{
    private readonly ClrModelManager _models;
    private readonly LinqConsoleService _console;
    private readonly ILogger _logger;
    private readonly string _connection;
    private readonly Lock _lock = new();
    private string? _model;
    private string? _consoleState;

    /// <param name="connection">The connection's name, in every line.</param>
    public ClrActivityLog(ClrModelManager models, LinqConsoleService console, ILogger logger, string connection)
    {
        _models = models;
        _console = console;
        _logger = logger;
        _connection = connection;
        _models.Changed += OnModelChanged;
        _console.Changed += OnConsoleChanged;
    }

    private void OnModelChanged()
    {
        var state = _models.State;
        var text = $"{state.Phase}{(state.BuildChanged ? " (build changed)" : "")}{(state.Step is { } step ? ": " + step : "")}";
        if (Changed(ref _model, text))
        {
            _logger.LogInformation("{Connection}: C# model {State}", _connection, text);
        }
    }

    private void OnConsoleChanged()
    {
        var state = _console.State;
        var text = $"{state.Phase}{(state.Step is { } step ? ": " + step : "")}{(state.Error is { } error ? " – " + error.Message : "")}";
        if (Changed(ref _consoleState, text))
        {
            _logger.LogInformation("{Connection}: LINQ console {State}", _connection, text);
        }
    }

    private bool Changed(ref string? last, string text)
    {
        lock (_lock)
        {
            if (last == text)
            {
                return false;
            }

            last = text;
            return true;
        }
    }

    public void Dispose()
    {
        _models.Changed -= OnModelChanged;
        _console.Changed -= OnConsoleChanged;
    }
}
