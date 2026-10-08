using FerretSharp.Core.Connections;

namespace FerretSharp.Core.Compare;

/// <summary>What became of one side: its snapshot, or why there is none.</summary>
/// <param name="NeedsPassword">No password is stored for the connection: ask for one and load this side again.</param>
public sealed record SideResult(SchemaSnapshot? Snapshot, DatabaseException? Error = null, bool NeedsPassword = false)
{
    public bool Loaded => Snapshot is not null;
}

/// <summary>
/// Reads the snapshots of the sides of a comparison (WP-20). There is exactly one active connection; every
/// connection of the comparison gets its own short-lived session (ACTION <see cref="SessionAction"/>), closed when its
/// sides are read. Sides of the same connection share the session; different connections load in parallel. A side
/// that fails (VPN, rights, wrong schema) does not stop the others.
/// </summary>
public sealed class SchemaCompareLoader(ConnectionManager connections, IDatabaseConnector connector)
{
    public const string SessionAction = "Schema-Vergleich";

    /// <param name="passwords">Passwords typed for this comparison only (connections without a stored one); not saved.</param>
    /// <param name="progress">Side index and its step (<c>Verbinde</c>, then the reader's steps); called on any thread.</param>
    /// <returns>One result per side, in order.</returns>
    /// <exception cref="OperationCanceledException">Cancelled; the sessions opened so far are closed.</exception>
    public async Task<IReadOnlyList<SideResult>> LoadAsync(
        IReadOnlyList<CompareSide> sides,
        IReadOnlyDictionary<Guid, string>? passwords,
        Action<int, string>? progress,
        CancellationToken cancellationToken)
    {
        var results = new SideResult[sides.Count];
        var groups = sides.Select((side, index) => (side, index)).GroupBy(s => s.side.ConnectionId);
        await Task.WhenAll(groups.Select(group => LoadConnectionAsync(group.Key, group.ToList(), passwords, progress, results, cancellationToken)));
        return results;
    }

    private async Task LoadConnectionAsync(
        Guid connectionId,
        IReadOnlyList<(CompareSide Side, int Index)> sides,
        IReadOnlyDictionary<Guid, string>? passwords,
        Action<int, string>? progress,
        SideResult[] results,
        CancellationToken cancellationToken)
    {
        await Task.Yield(); // the connections open in parallel, not one after another on the caller's thread

        void FailAll(SideResult result)
        {
            foreach (var (_, index) in sides)
            {
                results[index] = result;
            }
        }

        if (connections.Profiles.FirstOrDefault(p => p.Id == connectionId) is not { } profile)
        {
            FailAll(new SideResult(null, new DatabaseException("Diese Verbindung gibt es nicht mehr.")));
            return;
        }

        if ((passwords?.GetValueOrDefault(connectionId) ?? connections.GetPassword(connectionId)) is not { } password)
        {
            FailAll(new SideResult(null, new DatabaseException($"Für „{profile.Name}“ ist kein Passwort gespeichert."), NeedsPassword: true));
            return;
        }

        foreach (var (_, index) in sides)
        {
            progress?.Invoke(index, "Verbinde");
        }

        IDatabaseConnection connection;
        try
        {
            connection = await connector.OpenAsync(profile, password, SessionAction, cancellationToken);
        }
        catch (DatabaseException ex)
        {
            FailAll(new SideResult(null, ex));
            return;
        }

        await using (connection)
        {
            foreach (var (side, index) in sides)
            {
                try
                {
                    var snapshot = await connection.Schema.ReadSnapshotAsync(
                        side.OwnerFor(profile), new SyncProgress<string>(step => progress?.Invoke(index, step)), cancellationToken);
                    results[index] = new SideResult(snapshot);
                }
                catch (DatabaseException ex)
                {
                    results[index] = new SideResult(null, ex);
                    if (ex.IsConnectionLost)
                    {
                        // The session is gone: the remaining sides of this connection cannot be read either.
                        foreach (var (_, rest) in sides.Where(s => results[s.Index] is null))
                        {
                            results[rest] = new SideResult(null, ex);
                        }

                        return;
                    }
                }
            }
        }
    }
}
