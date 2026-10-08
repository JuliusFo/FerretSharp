using System.Text.Json;
using FerretSharp.Core.Connections;
using FerretSharp.Core.IO;

namespace FerretSharp.Core.Compare;

/// <summary>A side of a schema comparison: a connection and the schema to read on it.</summary>
/// <param name="Schema">As typed (normalized when read, <c>erp</c> → <c>ERP</c>); null = the profile's schema.</param>
public sealed record CompareSide(Guid ConnectionId, string? Schema = null)
{
    /// <summary>The schema to read for <paramref name="profile"/>.</summary>
    public string OwnerFor(ConnectionProfile profile) =>
        string.IsNullOrWhiteSpace(Schema) ? profile.EffectiveSchema : Oracle.OracleIdentifier.Normalize(Schema);
}

/// <summary>
/// A comparison the user saved to open again (WP-20, decision of the user), e.g. "ERP: Dev/Test/Prod": its sides, the
/// reference and the filters.
/// </summary>
/// <param name="Reference">Index into <see cref="Sides"/>; null = no reference.</param>
/// <param name="Kinds">Kinds of rows shown; null = all.</param>
public sealed record SavedComparison(
    Guid Id,
    string Name,
    IReadOnlyList<CompareSide> Sides,
    int? Reference = null,
    bool ColumnOrder = false,
    bool OnlyDifferences = true,
    IReadOnlyList<CompareKind>? Kinds = null)
{
    public const int MaxNameLength = 60;

    public CompareOptions Options => new(Reference, ColumnOrder);
}

/// <summary>
/// The saved comparisons in <c>comparisons.json</c>. A missing file means none; a broken one is reported and not
/// overwritten until the user saves again.
/// </summary>
public sealed class ComparisonStore(string filePath)
{
    private const int CurrentVersion = 1;

    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IReadOnlyList<SavedComparison>> LoadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(filePath))
            {
                return [];
            }

            await using var stream = File.OpenRead(filePath);
            var document = await JsonSerializer.DeserializeAsync<Document>(stream, JsonFiles.Options, cancellationToken);
            if (document?.Version > CurrentVersion)
            {
                throw new IOException(NewerFormat(document.Version));
            }

            return document?.Comparisons ?? [];
        }
        catch (JsonException ex)
        {
            throw new IOException($"{filePath} ist keine gültige Datei mit Schema-Vergleichen: {ex.Message}", ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(IReadOnlyList<SavedComparison> comparisons, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            // A file of a newer FerretSharp (used again after a downgrade) would lose what this version does not know.
            if (await VersionOnDiskAsync(cancellationToken) is { } version && version > CurrentVersion)
            {
                throw new IOException(NewerFormat(version) + " Sie wird nicht überschrieben.");
            }

            await AtomicJsonFile.WriteAsync(filePath, new Document(CurrentVersion, comparisons), JsonFiles.Options, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private string NewerFormat(int version) => $"{filePath} stammt aus einer neueren FerretSharp-Version (Format {version}).";

    /// <summary>The format of the file there; null if there is none or it cannot be read (then it may be replaced).</summary>
    private async Task<int?> VersionOnDiskAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(filePath);
            return (await JsonSerializer.DeserializeAsync<VersionOnly>(stream, JsonFiles.Options, cancellationToken))?.Version;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record Document(int Version, IReadOnlyList<SavedComparison> Comparisons);

    private sealed record VersionOnly(int Version);
}
