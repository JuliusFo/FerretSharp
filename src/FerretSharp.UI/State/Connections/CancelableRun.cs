namespace FerretSharp.UI.State;

/// <summary>
/// One cancelable run at a time – the "Test" of the connection dialog. The run owns its <see cref="CancellationTokenSource"/>:
/// it is created, cancelled and disposed only for that run. Before, "Test" kept one in a field and the run took it from there;
/// a second click while the build output was checked (the dialog did not render the spinner yet) started a second run over
/// the same field, the first one disposed the second's source and set it to null, and the second ended in a
/// NullReferenceException (3.41.0).
/// </summary>
public sealed class CancelableRun
{
    private CancellationTokenSource? _current;

    /// <summary>A run is going on; a further <see cref="TryRunAsync"/> starts nothing.</summary>
    public bool Running => _current is not null;

    /// <summary>Runs <paramref name="run"/> unless a run is going on.</summary>
    /// <returns>False if nothing was started. A cancelled run throws its <see cref="OperationCanceledException"/>.</returns>
    public async Task<bool> TryRunAsync(Func<CancellationToken, Task> run)
    {
        if (_current is not null)
        {
            return false;
        }

        using var cts = _current = new CancellationTokenSource();
        try
        {
            await run(cts.Token);
        }
        finally
        {
            _current = null;
        }

        return true;
    }

    /// <summary>Cancels the running run, if any (also when the dialog closes).</summary>
    public void Cancel() => _current?.Cancel();
}
