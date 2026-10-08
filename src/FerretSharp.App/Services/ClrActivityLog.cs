using FerretSharp.Core.ClrModel;
using Microsoft.Extensions.Logging;

namespace FerretSharp.App.Services;

/// <summary>
/// Logs the steps of loading the C# model and starting the LINQ console, so the times of UI stalls in the log can be
/// matched with what happened then (ADR 0016).
/// </summary>
public sealed class ClrActivityLog : IDisposable
{
    private readonly ClrModelManager _models;
    private readonly LinqConsoleService _console;
    private readonly ILogger<ClrActivityLog> _logger;
    private readonly Lock _lock = new();
    private string? _model;
    private string? _consoleState;

    public ClrActivityLog(ClrModelManager models, LinqConsoleService console, ILogger<ClrActivityLog> logger)
    {
        _models = models;
        _console = console;
        _logger = logger;
        _models.Changed += OnModelChanged;
        _console.Changed += OnConsoleChanged;
    }

    private void OnModelChanged()
    {
        var state = _models.State;
        var text = $"{state.Phase}{(state.BuildChanged ? " (build changed)" : "")}{(state.Step is { } step ? ": " + step : "")}";
        if (Changed(ref _model, text))
        {
            _logger.LogInformation("C# model {State}", text);
        }
    }

    private void OnConsoleChanged()
    {
        var state = _console.State;
        var text = $"{state.Phase}{(state.Step is { } step ? ": " + step : "")}{(state.Error is { } error ? " – " + error.Message : "")}";
        if (Changed(ref _consoleState, text))
        {
            _logger.LogInformation("LINQ console {State}", text);
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
