using System.Globalization;
using FerretSharp.Core.Resources;

namespace FerretSharp.Core.Connections.Transfer;

/// <summary>An installation of another tool whose connections can be imported.</summary>
/// <param name="Folder">DBeaver's workspace folder, or SQL Developer's folder with <c>connections.json</c>.</param>
/// <param name="Detail">Shown beside the source: DBeaver's projects, SQL Developer's version.</param>
/// <param name="Projects">DBeaver: the projects with connections, <see cref="DbeaverConnections.DefaultProject"/> first.</param>
public sealed record FoundInstallation(ConnectionImportSource Source, string Folder, string Detail, IReadOnlyList<string> Projects);

/// <summary>
/// Finds DBeaver and SQL Developer under the user's roaming app data (WP-35). Finding only looks at which folders and files
/// exist; nothing is read before the user picks the installation.
/// </summary>
public static class ConnectionInstallations
{
    /// <summary>DBeaver's workspace below the roaming app data.</summary>
    public static readonly string DbeaverWorkspace = Path.Combine("DBeaverData", "workspace6");

    public const string SqlDeveloperFolder = "SQL Developer";

    private const string SqlDeveloperSystemPrefix = "system";
    private const string SqlDeveloperConnectionsPrefix = "o.jdeveloper.db.connection";
    private const string SqlDeveloperConnectionsFile = "connections.json";

    /// <param name="appData">The roaming app data folder (<c>%APPDATA%</c>).</param>
    public static IReadOnlyList<FoundInstallation> Find(string appData)
    {
        // Each on its own: one that cannot even be listed is simply not offered, the other still is ("Choose file…" stays).
        return new[] { Try(() => FindDbeaver(Path.Combine(appData, DbeaverWorkspace))), Try(() => FindSqlDeveloper(Path.Combine(appData, SqlDeveloperFolder))) }
            .OfType<FoundInstallation>()
            .ToList();

        static FoundInstallation? Try(Func<FoundInstallation?> find)
        {
            try
            {
                return find();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    /// <exception cref="ConnectionImportException">A file or folder cannot be read, or a file has an unknown format.</exception>
    public static async Task<ImportFile> ReadAsync(FoundInstallation installation, CancellationToken cancellationToken)
    {
        try
        {
            return await ReadCoreAsync(installation, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Listing a folder (the files themselves are read through ImportLimits, which reports its own errors).
            throw new ConnectionImportException(TextFormat.Format(ConnectionText.ImportUnreadable, ex.Message), ex);
        }
    }

    private static async Task<ImportFile> ReadCoreAsync(FoundInstallation installation, CancellationToken cancellationToken)
    {
        if (installation.Source == ConnectionImportSource.SqlDeveloper)
        {
            var content = await ImportLimits.ReadFileAsync(Path.Combine(installation.Folder, SqlDeveloperConnectionsFile), cancellationToken);
            return SqlDeveloperConnections.Read(content);
        }

        var candidates = new List<ImportCandidate>();
        var others = 0;
        foreach (var project in installation.Projects)
        {
            var folder = Path.Combine(installation.Folder, project, ".dbeaver");
            var credentialsPath = Path.Combine(folder, DbeaverConnections.CredentialsFile);
            var users = DbeaverCredentials.ReadUsers(
                File.Exists(credentialsPath) ? await ImportLimits.ReadFileAsync(credentialsPath, cancellationToken) : null);
            foreach (var file in DataSources(folder))
            {
                var read = DbeaverConnections.Read(await ImportLimits.ReadFileAsync(file, cancellationToken), users, project);
                candidates.AddRange(read.Candidates);
                others += read.OtherDatabases;
            }
        }

        return new ImportFile(ConnectionImportSource.DBeaver, candidates, others);
    }

    private static FoundInstallation? FindDbeaver(string workspace)
    {
        if (!Directory.Exists(workspace))
        {
            return null;
        }

        var projects = Directory.EnumerateDirectories(workspace)
            .Where(project => DataSources(Path.Combine(project, ".dbeaver")).Any())
            .Select(Path.GetFileName)
            .OfType<string>()
            .OrderBy(name => name == DbeaverConnections.DefaultProject ? 0 : 1)
            .ThenBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return projects.Count == 0
            ? null
            : new FoundInstallation(ConnectionImportSource.DBeaver, workspace, string.Join(", ", projects), projects);
    }

    private static IEnumerable<string> DataSources(string folder) =>
        Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "data-sources*.json").Where(DbeaverConnections.IsDataSources).Order(StringComparer.Ordinal)
            : [];

    /// <summary>
    /// The newest <c>system&lt;version&gt;</c> folder with a <c>connections.json</c>: SQL Developer leaves the old ones
    /// behind after an update. Versions compare as numbers (<c>system9</c> is older than <c>system23</c>).
    /// </summary>
    private static FoundInstallation? FindSqlDeveloper(string root)
    {
        if (!Directory.Exists(root))
        {
            return null;
        }

        return Directory.EnumerateDirectories(root, SqlDeveloperSystemPrefix + "*")
            .Select(system => (Version: Path.GetFileName(system)[SqlDeveloperSystemPrefix.Length..], Folder: ConnectionsFolder(system)))
            .Where(s => s.Folder is not null && VersionParts(s.Version) is not null)
            .OrderByDescending(s => s.Version, VersionComparer.Instance)
            .Select(s => new FoundInstallation(ConnectionImportSource.SqlDeveloper, s.Folder!, s.Version, []))
            .FirstOrDefault();
    }

    /// <summary><c>o.jdeveloper.db.connection</c> (23.x, 24.x) or with a version suffix (older releases).</summary>
    private static string? ConnectionsFolder(string system) =>
        Directory.EnumerateDirectories(system, SqlDeveloperConnectionsPrefix + "*")
            .Where(folder => File.Exists(Path.Combine(folder, SqlDeveloperConnectionsFile)))
            .Order(StringComparer.Ordinal)
            .LastOrDefault();

    internal static int[]? VersionParts(string version)
    {
        var parts = version.Split('.');
        var numbers = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
            {
                return null;
            }
        }

        return numbers;
    }

    internal sealed class VersionComparer : IComparer<string>
    {
        public static readonly VersionComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            var a = VersionParts(x ?? "") ?? [];
            var b = VersionParts(y ?? "") ?? [];
            for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
            {
                var diff = (i < a.Length ? a[i] : 0).CompareTo(i < b.Length ? b[i] : 0);
                if (diff != 0)
                {
                    return diff;
                }
            }

            return 0;
        }
    }
}
