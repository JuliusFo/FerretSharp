using FerretSharp.Core.Resources;

namespace FerretSharp.Core.Connections;

/// <summary>No password is saved or entered for this run: ask for one, then try again.</summary>
public sealed class MissingPasswordException(ConnectionProfile profile)
    : DatabaseException(TextFormat.Format(ConnectionText.NoPasswordFor, profile.Name));

/// <summary>
/// Opens a session of its own beside the connection's main one – the read session (diff, time travel), a workspace's
/// session, the server load, the schema compare (review 3.70.0, B1: five copies that drifted apart). The open runs in
/// attempts (<see cref="SessionOpening"/>); a refused login is never tried again by it.
/// </summary>
public static class OwnSession
{
    /// <param name="readOnlySnapshots">A read-only profile (Prod by default): Oracle itself rejects DML in the session.</param>
    /// <exception cref="MissingPasswordException">No password for the connection.</exception>
    /// <exception cref="DatabaseException">Refused, unreachable or every attempt past its limit (<see cref="SessionOpenTimeoutException"/>).</exception>
    public static async Task<IDatabaseConnection> OpenAsync(
        IDatabaseConnector connector,
        ConnectionManager connections,
        ConnectionProfile profile,
        string action,
        TimeProvider time,
        bool readOnlySnapshots,
        CancellationToken cancellationToken)
    {
        var password = connections.GetPassword(profile.Id) ?? throw new MissingPasswordException(profile);
        IDatabaseConnection connection;
        try
        {
            connection = await SessionOpening.OpenAsync(
                (_, token) => connector.OpenAsync(profile, password, action, token), time, retrying: null, cancellationToken);
        }
        catch (DatabaseException ex) when (ex.ErrorCode == ConnectionManager.WrongPasswordCode)
        {
            // A password entered for this run is forgotten and asked for again. Tried again, each attempt counts towards
            // FAILED_LOGIN_ATTEMPTS – the server load's "Refresh" could lock the account (review 3.70.0, A5).
            connections.ForgetSessionPassword(profile.Id);
            throw;
        }

        connections.PasswordWorked(profile.Id); // a password entered with "save" is saved once it worked (A11)
        if (!readOnlySnapshots)
        {
            return connection;
        }

        try
        {
            await connection.UseReadOnlySnapshotsAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}
