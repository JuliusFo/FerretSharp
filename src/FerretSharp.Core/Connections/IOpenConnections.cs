namespace FerretSharp.Core.Connections;

/// <summary>
/// The connections open at the same time (WP-24): one is shown, the others stay connected in the background with their
/// workspaces, sessions and transactions.
/// </summary>
public interface IOpenConnections
{
    /// <summary>All open connections, the shown one included; may be read from any thread.</summary>
    IReadOnlyList<ActiveConnection> All { get; }
}
