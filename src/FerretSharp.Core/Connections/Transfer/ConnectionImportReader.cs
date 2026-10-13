using FerretSharp.Core.Resources;

namespace FerretSharp.Core.Connections.Transfer;

/// <summary>
/// Reads a file to import connections from (WP-35) and tells by its content where it comes from – not by its name: a
/// zip is a DBeaver project export, JSON is FerretSharp's, DBeaver's or SQL Developer's by its shape.
/// </summary>
public static class ConnectionImportReader
{
    /// <summary>The file extensions offered in the open dialog.</summary>
    public static IReadOnlyList<string> Extensions { get; } = ["json", "dbp"];

    /// <summary>
    /// Reads the file at <paramref name="path"/>. A DBeaver <c>data-sources.json</c> takes the users from the
    /// <c>credentials-config.json</c> beside it, if there is one.
    /// </summary>
    /// <exception cref="ConnectionImportException">Unreadable, too large or of an unknown format.</exception>
    public static async Task<ImportFile> ReadFileAsync(string path, CancellationToken cancellationToken)
    {
        var content = await ImportLimits.ReadFileAsync(path, cancellationToken);
        return Read(content, () =>
        {
            var credentials = Path.Combine(Path.GetDirectoryName(path) ?? "", DbeaverConnections.CredentialsFile);
            return File.Exists(credentials) ? ImportLimits.ReadFile(credentials) : null;
        }, ProjectOf(path));
    }

    /// <summary>Reads file content; a DBeaver <c>data-sources.json</c> on its own has no users.</summary>
    /// <exception cref="ConnectionImportException">Too large or of an unknown format.</exception>
    public static ImportFile Read(byte[] content)
    {
        if (content.LongLength > ImportLimits.MaxFileBytes)
        {
            throw new ConnectionImportException(ConnectionText.ImportFileTooLarge);
        }

        return Read(content, () => null, null);
    }

    /// <param name="dbeaverCredentials">Read only for a DBeaver file.</param>
    private static ImportFile Read(byte[] content, Func<byte[]?> dbeaverCredentials, string? project)
    {
        if (IsZip(content))
        {
            return DbeaverConnections.ReadExport(content);
        }

        using var document = ImportJson.Parse(content);
        var root = document.RootElement;
        if (FerretSharpConnections.Recognizes(root))
        {
            return FerretSharpConnections.Read(root);
        }

        if (SqlDeveloperConnections.Recognizes(root))
        {
            return SqlDeveloperConnections.Read(root);
        }

        if (DbeaverConnections.Recognizes(root))
        {
            var users = DbeaverCredentials.ReadUsers(dbeaverCredentials());
            return DbeaverConnections.Read(root, users, project);
        }

        throw new ConnectionImportException(ConnectionText.ImportUnknownFormat);
    }

    private static bool IsZip(byte[] content) => content is [0x50, 0x4b, 0x03, 0x04, ..];

    /// <summary><c>…\workspace6\&lt;project&gt;\.dbeaver\data-sources.json</c> → the project; null for any other place.</summary>
    private static string? ProjectOf(string path)
    {
        var folder = Path.GetDirectoryName(path);
        return folder is not null && Path.GetFileName(folder) == ".dbeaver"
            ? Path.GetFileName(Path.GetDirectoryName(folder))
            : null;
    }
}
