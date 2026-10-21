namespace FerretSharp.UI.State;

/// <summary>
/// Something to open once its connection is shown with its workspaces – a confirmed link (WP-37) or a script for the SQL
/// editor (WP-22, the schema comparison and the app statements). Only the latest request waits. It never opens later on
/// its own once the user went elsewhere or connecting failed or was cancelled (review of WP-37; review 3.40.0, A17: a SQL
/// tab opened hours later when the user connected by hand). R6b (B8): the rules were written twice in the shell.
/// </summary>
public sealed class PendingOpen<T>
    where T : class
{
    private (Guid ConnectionId, T Item)? _waiting;

    public void Wait(Guid connectionId, T item) => _waiting = (connectionId, item);

    public bool WaitsFor(Guid connectionId) => _waiting?.ConnectionId == connectionId;

    /// <summary>What waits for <paramref name="shown"/>, which no longer waits then; null if nothing does.</summary>
    public T? Take(Guid? shown)
    {
        if (_waiting is not { } waiting || waiting.ConnectionId != shown)
        {
            return null;
        }

        _waiting = null;
        return waiting.Item;
    }

    /// <summary>Connecting to <paramref name="connectionId"/> failed or was cancelled.</summary>
    public void DropFor(Guid connectionId)
    {
        if (WaitsFor(connectionId))
        {
            _waiting = null;
        }
    }

    /// <summary>The user went to another connection.</summary>
    public void DropExcept(Guid? shown)
    {
        if (_waiting is { } waiting && waiting.ConnectionId != shown)
        {
            _waiting = null;
        }
    }
}

/// <summary>
/// The failure each connection reported last (review of WP-37): the error of the last attempt to connect, or why it was
/// lost. Only a new one counts – compared by reference, since each attempt fails with an error of its own: a request
/// confirmed for a connection that had failed before waits for the next attempt.
/// </summary>
public sealed class ConnectionFailures
{
    private readonly Dictionary<Guid, object> _failures = [];

    /// <param name="failure">Null: the connection has no failure (connected).</param>
    /// <param name="dropFor">Called for a new failure: what waits for the connection no longer opens.</param>
    public void Report(Guid connectionId, object? failure, Action<Guid> dropFor)
    {
        if (failure is null)
        {
            _failures.Remove(connectionId);
            return;
        }

        if (_failures.TryGetValue(connectionId, out var known) && ReferenceEquals(known, failure))
        {
            return;
        }

        _failures[connectionId] = failure;
        dropFor(connectionId);
    }
}
