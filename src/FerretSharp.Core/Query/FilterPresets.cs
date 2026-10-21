using FerretSharp.Core.IO;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Query;

/// <summary>
/// A named set of filter rows, optionally with a sort order, saved to apply again (WP-37), e.g. "Open orders". Keyed by
/// the table name without owner like the display columns (ADR 0022): the schema is the same on Prod, Test and Dev, its
/// name may differ – a preset applies on every connection.
/// </summary>
/// <param name="Table">The table's name (<see cref="TableSummary.Name"/>), compared ordinally like Oracle names.</param>
/// <param name="Sorts">Empty: the preset keeps the sort order of the tab it is applied to.</param>
public sealed record FilterPreset(Guid Id, string Name, string Table, IReadOnlyList<FilterCondition> Filters, IReadOnlyList<SortSpec> Sorts)
{
    public const int MaxNameLength = 60;
}

/// <summary>A preset applied to a table: the filter rows and sort order for the tab, and what did not fit.</summary>
/// <param name="Sorts">Null: keep the tab's sort order (the preset has none, or none of its columns can be sorted here).</param>
/// <param name="MissingColumns">Columns of the preset the table does not have (another version of the schema); their rows come disabled.</param>
public sealed record AppliedPreset(IReadOnlyList<FilterCondition> Filters, IReadOnlyList<SortSpec>? Sorts, IReadOnlyList<string> MissingColumns);

/// <summary>Rules of filter presets (WP-37); the filter model itself stays as it is (LINQ-translatable).</summary>
public static class FilterPresets
{
    /// <summary>The key a table's presets are saved under.</summary>
    public static string TableKey(TableSummary table) => table.Name;

    /// <summary>Same name for the same table, ignoring case: "Offen" and "offen" would be indistinguishable in the menu.</summary>
    public static bool SameName(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.CurrentCultureIgnoreCase);

    /// <returns>A user-facing error for a preset name, or null.</returns>
    public static string? NameProblem(string name)
    {
        var trimmed = name.Trim();
        return trimmed.Length == 0 ? QueryText.PresetNameMissing
            : trimmed.Length > FilterPreset.MaxNameLength ? TextFormat.Format(QueryText.PresetNameTooLong, FilterPreset.MaxNameLength)
            : null;
    }

    /// <summary>
    /// Whether the tab's rows (as edited, with disabled ones) and sort order differ from what applying
    /// <paramref name="preset"/> gives – "Open orders · changed" in the filter bar. A preset without a sort order (or none
    /// that fits the table) does not mind the tab's.
    /// </summary>
    public static bool Differs(FilterPreset preset, TableDetails table, IReadOnlyList<FilterCondition> filters, IReadOnlyList<SortSpec> sorts)
    {
        // Group ids only tell groups apart: the filter bar numbers them anew (FilterGroups.Normalize).
        var applied = Apply(preset, table, [], append: false);
        var wanted = FilterGroups.Normalize(applied.Filters);
        var current = FilterGroups.Normalize(filters);
        return wanted.Count != current.Count
               || wanted.Zip(current).Any(pair => !Same(pair.First, pair.Second))
               || applied.Sorts is { } presetSorts && !presetSorts.SequenceEqual(sorts);
    }

    private static bool Same(FilterCondition a, FilterCondition b) =>
        a.Column == b.Column && a.Op == b.Op && a.Enabled == b.Enabled && a.Group == b.Group && a.Values.SequenceEqual(b.Values, StringComparer.Ordinal);

    /// <summary>
    /// The filter rows and sort order of <paramref name="preset"/> for <paramref name="table"/>: replacing the current
    /// rows, or after them (<paramref name="append"/>; its groups get new ids). Rows on columns the table does not have come disabled (and are
    /// listed), so the user sees what the preset wanted; sorts on such columns, or on ones that cannot be sorted, are left out.
    /// </summary>
    public static AppliedPreset Apply(FilterPreset preset, TableDetails table, IReadOnlyList<FilterCondition> current, bool append)
    {
        var fitted = Fit(table, preset.Filters, preset.Sorts);
        return append ? fitted with { Filters = FilterGroups.Normalize([.. current, .. FilterGroups.Renumber(current, fitted.Filters)]) } : fitted;
    }

    /// <summary>
    /// Filter rows and a sort order made for another version of <paramref name="table"/> (a preset, a link from another
    /// database – WP-37), fitted to this one: rows on columns it does not have come disabled (and are listed), sorts on
    /// such columns or on ones that cannot be sorted are left out (null if none is left).
    /// </summary>
    public static AppliedPreset Fit(TableDetails table, IReadOnlyList<FilterCondition> wanted, IReadOnlyList<SortSpec> wantedSorts)
    {
        var missing = new List<string>();
        var filters = new List<FilterCondition>();
        foreach (var filter in wanted)
        {
            if (table.Columns.Any(c => c.Name == filter.Column))
            {
                filters.Add(filter);
                continue;
            }

            if (!missing.Contains(filter.Column))
            {
                missing.Add(filter.Column);
            }

            filters.Add(filter with { Enabled = false });
        }

        var sorts = wantedSorts
            .Where(s => table.Columns.FirstOrDefault(c => c.Name == s.Column) is { } column && ColumnCategories.IsSortable(ColumnCategories.Of(column)))
            .DistinctBy(s => s.Column)
            .ToList();
        foreach (var sort in wantedSorts.Where(s => table.Columns.All(c => c.Name != s.Column) && !missing.Contains(s.Column)))
        {
            missing.Add(sort.Column);
        }

        return new(filters, sorts.Count == 0 ? null : sorts, missing);
    }
}

/// <summary>
/// The filter presets in <c>filter-presets.json</c> in the data folder (WP-37). A missing file means none. A broken file
/// or one of a newer FerretSharp is reported and never overwritten – it holds the user's presets for every table.
/// </summary>
public sealed class FilterPresetStore(string filePath)
{
    private const int CurrentVersion = 1;

    private readonly SemaphoreSlim _gate = new(1, 1);

    public string FilePath => filePath;

    /// <exception cref="IOException">The file is broken, unreadable or of a newer version.</exception>
    public async Task<IReadOnlyList<FilterPreset>> LoadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var read = await ReadAsync(cancellationToken);
            return read.State switch
            {
                JsonFileState.Missing or JsonFileState.Empty => [],
                JsonFileState.Ok => read.Document!.Presets!.OfType<FilterPreset>().ToList(),
                _ => throw Problem(read),
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <exception cref="IOException">The file on disk is broken or of a newer version (it is kept), or cannot be written.</exception>
    public async Task SaveAsync(IReadOnlyList<FilterPreset> presets, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var read = await ReadAsync(cancellationToken);
            if (read.State is JsonFileState.Broken or JsonFileState.Newer)
            {
                throw Problem(read, saving: true);
            }

            await AtomicJsonFile.WriteAsync(filePath, new Document(CurrentVersion, presets), JsonFiles.Options, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private Task<JsonFileRead<Document>> ReadAsync(CancellationToken cancellationToken) =>
        VersionedJsonFile.ReadAsync<Document>(filePath, CurrentVersion, JsonFiles.Options, cancellationToken, normalize: Normalize);

    /// <summary>Entries edited by hand: presets without name or table are left out, lists set to null become empty.</summary>
    private static Document Normalize(Document document) => document with
    {
        Presets = (document.Presets ?? [])
            .Where(p => p is { Name: not null, Table: not null })
            .Select(p => p! with
            {
                Filters = (p.Filters ?? []).Where(f => f?.Column is not null).Select(f => f! with { Values = f.Values ?? [] }).ToList(),
                Sorts = (p.Sorts ?? []).Where(s => s?.Column is not null).Select(s => s!).ToList(),
            })
            .ToList(),
    };

    private IOException Problem(JsonFileRead<Document> read, bool saving = false) => read.State switch
    {
        JsonFileState.Newer => new IOException(TextFormat.Format(saving ? CoreText.FileFromNewerVersionNotOverwritten : CoreText.FileFromNewerVersion, filePath, read.Version)),
        JsonFileState.Broken => new IOException(TextFormat.Format(saving ? QueryText.PresetFileInvalidNotOverwritten : QueryText.PresetFileInvalid, filePath, read.Error!.Message), read.Error),
        _ => new IOException(TextFormat.Format(QueryText.PresetFileUnreadable, filePath, read.Error?.Message), read.Error),
    };

    private sealed record Document(int Version, IReadOnlyList<FilterPreset?>? Presets);
}
