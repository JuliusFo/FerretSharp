namespace FerretSharp.Core.Connections.Transfer;

/// <summary>Where imported connections come from (WP-35).</summary>
public enum ConnectionImportSource
{
    FerretSharp,
    DBeaver,
    SqlDeveloper,
}

/// <summary>
/// One connection found in a file. <see cref="Profile"/> is what would be saved (a new id; <see cref="ConnectionProfile.Group"/>
/// null when the source has none, <see cref="ConnectionProfile.User"/> empty when the source did not say,
/// <see cref="ConnectionProfile.ReadOnly"/> only what the source itself says – the preview adds Prod and guessed kinds,
/// see <see cref="ImportRow.ReadOnly"/>) – or null when FerretSharp cannot connect that way, with the reason in
/// <see cref="Unsupported"/>.
/// </summary>
/// <param name="Name">The name in the source.</param>
/// <param name="Address">The address as the preview shows it, also for connections that cannot be imported.</param>
/// <param name="KindGuessed">The source says nothing about the environment: the kind was guessed from the name.</param>
public sealed record ImportCandidate(
    string Name,
    string Address,
    ConnectionProfile? Profile,
    bool KindGuessed = false,
    string? Unsupported = null);

/// <summary>What a file or an installation holds.</summary>
/// <param name="OtherDatabases">Connections to other databases than Oracle; they are left out.</param>
public sealed record ImportFile(ConnectionImportSource Source, IReadOnlyList<ImportCandidate> Candidates, int OtherDatabases = 0);

/// <summary>A file that cannot be imported; the message is for the user.</summary>
public sealed class ConnectionImportException(string message, Exception? inner = null) : Exception(message, inner);
