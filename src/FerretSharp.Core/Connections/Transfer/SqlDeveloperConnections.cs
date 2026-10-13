using System.Globalization;
using System.Text.Json;
using FerretSharp.Core.Resources;

namespace FerretSharp.Core.Connections.Transfer;

/// <summary>
/// Oracle connections from SQL Developer (WP-35): its "Export Connections" file and its own <c>connections.json</c> share
/// one format – <c>connections[]</c> with <c>name</c> and <c>info</c>, every value a string. A saved <c>password</c> in
/// <c>info</c> is never read. SQL Developer has no environment, so the kind is always guessed from the name.
/// </summary>
public static class SqlDeveloperConnections
{
    /// <summary>Whether a parsed JSON file is SQL Developer's: <c>connections</c> is an array of entries with <c>info</c>.</summary>
    internal static bool Recognizes(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty("connections", out var connections)
        && connections.ValueKind == JsonValueKind.Array
        && connections.EnumerateArray().Any(c => c.ValueKind == JsonValueKind.Object && c.TryGetProperty("info", out _));

    internal static ImportFile Read(byte[] json)
    {
        using var document = ImportJson.Parse(json);
        return Read(document.RootElement);
    }

    internal static ImportFile Read(JsonElement root)
    {
        if (!Recognizes(root))
        {
            throw new ConnectionImportException(ConnectionText.ImportUnknownFormat);
        }

        var candidates = new List<ImportCandidate>();
        var others = 0;
        foreach (var connection in root.GetProperty("connections").EnumerateArray())
        {
            if (connection.ValueKind != JsonValueKind.Object
                || !connection.TryGetProperty("info", out var info) || info.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (!IsOracle(info))
            {
                others++;
                continue;
            }

            candidates.Add(Candidate(ImportJson.Text(connection, "name") ?? ImportJson.Text(info, "ConnName") ?? "?", info));
        }

        return new ImportFile(ConnectionImportSource.SqlDeveloper, candidates, others);
    }

    /// <summary>
    /// Oracle connections say <c>RaptorConnectionType: Oracle</c> (or nothing, in older files) and use the Oracle driver;
    /// third-party connections (MySQL, SQL Server …) name their own.
    /// </summary>
    private static bool IsOracle(JsonElement info) =>
        (ImportJson.Text(info, "RaptorConnectionType") ?? "Oracle").Equals("Oracle", StringComparison.OrdinalIgnoreCase)
        && (ImportJson.Text(info, "driver") ?? "oracle.").StartsWith("oracle.", StringComparison.OrdinalIgnoreCase);

    private static ImportCandidate Candidate(string name, JsonElement info)
    {
        var url = ImportJson.Text(info, "customUrl");
        var type = ImportJson.Text(info, "OracleConnectionType") ?? "BASIC";
        if (type.Equals("TNS", StringComparison.OrdinalIgnoreCase))
        {
            var alias = ImportJson.Text(info, "tnsName") ?? TnsAliasOf(url) ?? url ?? "";
            return new ImportCandidate(name, $"TNS: {alias}", null, Unsupported: ConnectionText.ImportTnsUnsupported);
        }

        var address = type.Equals("BASIC", StringComparison.OrdinalIgnoreCase) ? BasicAddress(info) : JdbcUrl.TryParse(url);
        var shown = address?.Display ?? url ?? ImportJson.Text(info, "hostname") ?? "";
        if (address is null)
        {
            return new ImportCandidate(name, shown, null,
                Unsupported: type.Equals("BASIC", StringComparison.OrdinalIgnoreCase)
                    ? ConnectionText.ImportAddressIncomplete
                    : ConnectionText.ImportUrlUnsupported);
        }

        if (Unsupported(info) is { } reason)
        {
            return new ImportCandidate(name, shown, null, Unsupported: reason);
        }

        var kind = ConnectionKindGuess.FromName(name);
        var profile = new ConnectionProfile(Guid.NewGuid(), name, kind, address, ImportJson.Text(info, "user") ?? "", null, ReadOnly: false);
        return new ImportCandidate(name, shown, profile, KindGuessed: true);
    }

    /// <summary><c>jdbc:oracle:thin:@REPDB</c> → <c>REPDB</c>, for showing a TNS connection.</summary>
    private static string? TnsAliasOf(string? url)
    {
        const string Prefix = "jdbc:oracle:thin:@";
        return url is not null && url.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) ? url[Prefix.Length..] : null;
    }

    private static HostPortAddress? BasicAddress(JsonElement info)
    {
        var host = ImportJson.Text(info, "hostname");
        var service = ImportJson.Text(info, "serviceName");
        var sid = ImportJson.Text(info, "sid");
        if (string.IsNullOrWhiteSpace(host) || (string.IsNullOrWhiteSpace(service) && string.IsNullOrWhiteSpace(sid)))
        {
            return null;
        }

        var portText = ImportJson.Text(info, "port");
        var port = HostPortAddress.DefaultPort;
        if (!string.IsNullOrWhiteSpace(portText)
            && !int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port))
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(service)
            ? new HostPortAddress(host.Trim(), port, null, sid!.Trim())
            : new HostPortAddress(host.Trim(), port, service.Trim(), null);
    }

    /// <summary>Ways of logging in FerretSharp does not offer (it always logs in with user and password).</summary>
    private static string? Unsupported(JsonElement info)
    {
        if (IsTrue(info, "OS_AUTHENTICATION"))
        {
            return ConnectionText.ImportOsAuthUnsupported;
        }

        if (IsTrue(info, "KERBEROS_AUTHENTICATION"))
        {
            return ConnectionText.ImportKerberosUnsupported;
        }

        if (IsTrue(info, "IS_PROXY"))
        {
            return ConnectionText.ImportProxyUnsupported;
        }

        return ImportJson.Text(info, "role") is { } role && !role.Equals("default", StringComparison.OrdinalIgnoreCase)
            ? TextFormat.Format(ConnectionText.ImportRoleUnsupported, role)
            : null;
    }

    private static bool IsTrue(JsonElement info, string property) =>
        string.Equals(ImportJson.Text(info, property), "true", StringComparison.OrdinalIgnoreCase);
}
