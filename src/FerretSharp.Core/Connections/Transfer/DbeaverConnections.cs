using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using FerretSharp.Core.Resources;

namespace FerretSharp.Core.Connections.Transfer;

/// <summary>
/// Oracle connections from DBeaver (WP-35): a project's <c>.dbeaver/data-sources.json</c>, with the users from the
/// <c>credentials-config.json</c> beside it, or a project export (<c>.dbp</c>, a zip with <c>projects/&lt;project&gt;/.dbeaver/…</c>).
/// </summary>
public static class DbeaverConnections
{
    /// <summary>DBeaver's default project; its connections get the group the user picks in the preview.</summary>
    public const string DefaultProject = "General";

    internal const string CredentialsFile = "credentials-config.json";

    /// <summary>A project export: every project in it, each with its credentials.</summary>
    internal static ImportFile ReadExport(byte[] zip)
    {
        var candidates = new List<ImportCandidate>();
        var others = 0;
        try
        {
            using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
            var entries = archive.Entries.Take(ImportLimits.MaxZipEntries + 1).ToList();
            if (entries.Count > ImportLimits.MaxZipEntries)
            {
                throw new ConnectionImportException(ConnectionText.ImportUnknownFormat);
            }

            foreach (var project in entries.Select(ProjectOf).OfType<string>().Distinct(StringComparer.Ordinal))
            {
                var folder = $"projects/{project}/.dbeaver/";
                var credentials = entries.FirstOrDefault(e => e.FullName == folder + CredentialsFile);
                var users = DbeaverCredentials.ReadUsers(credentials is null ? null : ImportLimits.ReadEntry(credentials));
                foreach (var dataSources in entries.Where(e => IsDataSources(e.FullName, folder)))
                {
                    var file = Read(ImportLimits.ReadEntry(dataSources), users, project);
                    candidates.AddRange(file.Candidates);
                    others += file.OtherDatabases;
                }
            }
        }
        catch (InvalidDataException ex)
        {
            throw new ConnectionImportException(TextFormat.Format(ConnectionText.ImportUnreadable, ex.Message), ex);
        }

        return new ImportFile(ConnectionImportSource.DBeaver, candidates, others);
    }

    /// <summary>One <c>data-sources*.json</c>.</summary>
    /// <param name="users">From the project's credentials (<see cref="DbeaverCredentials.ReadUsers"/>).</param>
    /// <param name="project">The project's name; null when unknown (a file picked on its own).</param>
    internal static ImportFile Read(byte[] json, IReadOnlyDictionary<string, string> users, string? project)
    {
        using var document = ImportJson.Parse(json);
        return Read(document.RootElement, users, project);
    }

    internal static ImportFile Read(JsonElement root, IReadOnlyDictionary<string, string> users, string? project)
    {
        if (!root.TryGetProperty("connections", out var connections) || connections.ValueKind != JsonValueKind.Object)
        {
            throw new ConnectionImportException(ConnectionText.ImportUnknownFormat);
        }

        var types = root.TryGetProperty("connection-types", out var t) && t.ValueKind == JsonValueKind.Object ? t : default;
        var projectGroup = project is null || project == DefaultProject ? null : project;
        var candidates = new List<ImportCandidate>();
        var others = 0;
        foreach (var connection in connections.EnumerateObject())
        {
            if (connection.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (!IsOracle(connection.Value))
            {
                others++;
                continue;
            }

            candidates.Add(Candidate(connection.Name, connection.Value, types, users, projectGroup));
        }

        return new ImportFile(ConnectionImportSource.DBeaver, candidates, others);
    }

    /// <summary>Whether a parsed JSON file is DBeaver's: <c>connections</c> is an object keyed by id.</summary>
    internal static bool Recognizes(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty("connections", out var connections)
        && connections.ValueKind == JsonValueKind.Object;

    internal static bool IsDataSources(string fileName) =>
        Path.GetFileName(fileName).StartsWith("data-sources", StringComparison.Ordinal)
        && fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

    private static bool IsDataSources(string entry, string folder) =>
        entry.StartsWith(folder, StringComparison.Ordinal)
        && !entry.AsSpan(folder.Length).Contains('/')
        && IsDataSources(entry);

    /// <summary><c>projects/&lt;project&gt;/…</c> → the project.</summary>
    private static string? ProjectOf(ZipArchiveEntry entry)
    {
        var parts = entry.FullName.Split('/');
        return parts.Length > 2 && parts[0] == "projects" && parts[1].Length > 0 ? parts[1] : null;
    }

    private static bool IsOracle(JsonElement connection) =>
        string.Equals(ImportJson.Text(connection, "provider"), "oracle", StringComparison.OrdinalIgnoreCase)
        || (ImportJson.Text(connection, "driver") ?? "").StartsWith("oracle", StringComparison.OrdinalIgnoreCase);

    private static ImportCandidate Candidate(
        string id, JsonElement connection, JsonElement types, IReadOnlyDictionary<string, string> users, string? projectGroup)
    {
        var name = ImportJson.Text(connection, "name") ?? id;
        var config = connection.TryGetProperty("configuration", out var c) && c.ValueKind == JsonValueKind.Object ? c : default;
        var properties = config.ValueKind == JsonValueKind.Object && config.TryGetProperty("provider-properties", out var p) ? p : default;
        var connectionType = ImportJson.Text(properties, "@dbeaver-connection-type@");
        var url = ImportJson.Text(config, "url");

        if (string.Equals(connectionType, "TNS", StringComparison.OrdinalIgnoreCase))
        {
            var alias = ImportJson.Text(config, "database") ?? "";
            return new ImportCandidate(name, $"TNS: {alias}", null, Unsupported: ConnectionText.ImportTnsUnsupported);
        }

        HostPortAddress? address;
        if (string.Equals(connectionType, "CUSTOM", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ImportJson.Text(config, "configurationType"), "URL", StringComparison.OrdinalIgnoreCase))
        {
            address = JdbcUrl.TryParse(url);
            if (address is null)
            {
                return new ImportCandidate(name, url ?? "", null, Unsupported: ConnectionText.ImportUrlUnsupported);
            }
        }
        else
        {
            address = BasicAddress(config, properties);
            if (address is null)
            {
                return new ImportCandidate(name, url ?? ImportJson.Text(config, "host") ?? "", null,
                    Unsupported: ConnectionText.ImportAddressIncomplete);
            }
        }

        var (kind, guessed) = Kind(ImportJson.Text(config, "type"), types, name);
        var user = ImportJson.Text(config, "user") ?? users.GetValueOrDefault(id) ?? "";
        var readOnly = connection.TryGetProperty("read-only", out var ro) && ro.ValueKind == JsonValueKind.True;
        var group = ImportJson.Text(connection, "folder") ?? projectGroup;
        var profile = new ConnectionProfile(Guid.NewGuid(), name, kind, address, user, null, readOnly, group);
        return new ImportCandidate(name, address.Display, profile, guessed);
    }

    private static HostPortAddress? BasicAddress(JsonElement config, JsonElement properties)
    {
        var host = ImportJson.Text(config, "host");
        var database = ImportJson.Text(config, "database");
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(database))
        {
            return null;
        }

        var portText = ImportJson.Text(config, "port");
        var port = HostPortAddress.DefaultPort;
        if (!string.IsNullOrWhiteSpace(portText)
            && !int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port))
        {
            return null;
        }

        return string.Equals(ImportJson.Text(properties, "@dbeaver-sid-service@"), "SID", StringComparison.OrdinalIgnoreCase)
            ? new HostPortAddress(host.Trim(), port, null, database.Trim())
            : new HostPortAddress(host.Trim(), port, database.Trim(), null);
    }

    /// <summary>
    /// DBeaver's built-in types <c>dev</c>/<c>test</c>/<c>prod</c> say the environment. An own type is guessed from its
    /// name, then from the connection's name – and counts as guessed either way.
    /// </summary>
    private static (ConnectionKind Kind, bool Guessed) Kind(string? type, JsonElement types, string name)
    {
        switch (type?.ToLowerInvariant())
        {
            case "dev":
                return (ConnectionKind.Dev, false);
            case "test":
                return (ConnectionKind.Test, false);
            case "prod":
                return (ConnectionKind.Prod, false);
        }

        var typeName = type is not null && types.ValueKind == JsonValueKind.Object && types.TryGetProperty(type, out var definition)
            ? ImportJson.Text(definition, "name")
            : null;
        var fromType = ConnectionKindGuess.FromName(typeName ?? type);
        return (fromType != ConnectionKind.Other ? fromType : ConnectionKindGuess.FromName(name), true);
    }
}
