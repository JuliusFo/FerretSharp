using System.Text.Json;
using FerretSharp.Core.IO;
using FerretSharp.Core.Resources;

namespace FerretSharp.Core.Connections.Transfer;

/// <summary>
/// FerretSharp's own exchange format (WP-35): the document of <c>connections.json</c> plus a <c>format</c> marker, so
/// another installation's <c>connections.json</c> imports just as well as an export. Passwords are never part of either.
/// </summary>
public static class FerretSharpConnections
{
    public const string Format = "ferretsharp-connections";

    /// <summary>The suggested file name of an export.</summary>
    public const string FileName = "ferretsharp-connections.json";

    /// <param name="includeLocalPaths">
    /// Keep the paths that only make sense on this computer (<see cref="ConnectionProfile.LocalPaths"/>: linked C# project and
    /// the solution chosen for it, model file, script folder, TNS_ADMIN, audit template) – for moving to a new computer of one's own; left
    /// out when passing connections on to colleagues.
    /// </param>
    public static string Export(IEnumerable<ConnectionProfile> profiles, bool includeLocalPaths)
    {
        var connections = profiles
            .Select(p => includeLocalPaths ? p : p.WithoutLocalPaths())
            .ToList();
        return JsonSerializer.Serialize(new ExportDocument(Format, ConnectionStore.CurrentVersion, connections), JsonFiles.Options);
    }

    /// <summary>Whether a parsed JSON file is FerretSharp's: the marker, or connections with an <c>address</c>.</summary>
    internal static bool Recognizes(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object
        && (ImportJson.Text(root, "format") == Format
            || (root.TryGetProperty("connections", out var connections)
                && connections.ValueKind == JsonValueKind.Array
                && root.TryGetProperty("version", out _)
                && connections.EnumerateArray().All(c => c.ValueKind == JsonValueKind.Object && c.TryGetProperty("address", out _))));

    /// <summary>Every connection gets a new id; Prod is read-only whatever the file says (<see cref="ImportRow.ReadOnly"/>).</summary>
    internal static ImportFile Read(JsonElement root)
    {
        // The version first: a newer file may hold shapes this version cannot deserialize (a new address type).
        if (root.TryGetProperty("version", out var version) && version.TryGetInt32(out var number) && number > ConnectionStore.CurrentVersion)
        {
            throw new ConnectionImportException(TextFormat.Format(ConnectionText.ImportNewerVersion, number));
        }

        ExportDocument? document;
        try
        {
            document = root.Deserialize<ExportDocument>(JsonFiles.Options);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            // NotSupportedException: an address without "type", or with "type" not first (edited by hand).
            throw new ConnectionImportException(TextFormat.Format(ConnectionText.ImportInvalidJson, ex.Message), ex);
        }

        if (document is null)
        {
            throw new ConnectionImportException(ConnectionText.ImportUnknownFormat);
        }

        var candidates = (document.Connections ?? [])
            .Where(p => p is { Address: not null })
            .Select(p => p with { Id = Guid.NewGuid() })
            .Select(p => new ImportCandidate(p.Name, p.Address.Display + (p.Address is TnsAliasAddress ? " (TNS)" : ""), p))
            .ToList();
        return new ImportFile(ConnectionImportSource.FerretSharp, candidates);
    }

    /// <param name="Format">Null in a <c>connections.json</c>, <see cref="FerretSharpConnections.Format"/> in an export.</param>
    private sealed record ExportDocument(string? Format, int Version, IReadOnlyList<ConnectionProfile>? Connections);
}
