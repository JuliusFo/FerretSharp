namespace FerretSharp.UI.State;

/// <summary>
/// The grant and revoke actions of the two grant views (WP-71; review 3.70.0, B6 – the fix of the third pass stood in both):
/// one at a time. A new one, closing the draft or the tab gives up the read of the one before, and its result no longer
/// applies – it came late over the VPN and stood under the revoke of another object.
/// </summary>
public sealed class GrantActions : IDisposable
{
    private readonly CancellationTokenSource _closing = new();
    private CancellationTokenSource? _action;
    private bool _disposed;

    /// <summary>The current action reads (users, synonyms): its buttons wait.</summary>
    public bool Reading { get; private set; }

    /// <summary>A grant or revoke opens, or the draft closes: the one before is given up.</summary>
    /// <returns>The token of the new action; cancelled once the tab has closed (OnRan of a run that ended after it).</returns>
    public CancellationToken Next()
    {
        if (_disposed)
        {
            return new CancellationToken(canceled: true);
        }

        _action?.Cancel();
        _action?.Dispose();
        _action = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        Reading = false;
        return _action.Token;
    }

    public void StartReading() => Reading = true;

    public void DoneReading() => Reading = false;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _closing.Cancel();
        _action?.Dispose();
        _closing.Dispose();
    }
}
