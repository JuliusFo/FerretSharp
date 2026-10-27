using System.Text.Json;
using System.Text.Json.Serialization;
using FerretSharp.Core.IO;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Display;

/// <summary>
/// The display columns of each table (WP-31): their values stand beside every foreign key to the table
/// (<c>4711 · PetersPizza</c>). Since WP-65 a table may have several, joined in their order (<c>4711 · Peters - Pizza</c>).
/// Keyed by table name without owner – the schema is the same on Prod, Test and Dev, the schema name may differ (the
/// user's decision).
/// </summary>
public sealed class DisplayColumns
{
    public static readonly DisplayColumns Empty = new(new SortedDictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal));

    private readonly SortedDictionary<string, IReadOnlyList<string>> _columns;

    private DisplayColumns(SortedDictionary<string, IReadOnlyList<string>> columns) => _columns = columns;

    /// <summary>Table name → its display columns in order (at least one), sorted by table name (as written to the file).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> All => _columns;

    /// <summary>Tables with display columns.</summary>
    public int Count => _columns.Count;

    /// <summary>The display columns of a table by its name, in order; empty without any.</summary>
    public IReadOnlyList<string> ColumnsOf(string table) => _columns.GetValueOrDefault(table) ?? [];

    /// <summary>
    /// The display columns of a table as indexes into its columns, in order – those the table has (an entry for another
    /// database is ignored); empty without any.
    /// </summary>
    public IReadOnlyList<int> IndexesIn(TableDetails table) =>
        [.. ColumnsOf(table.Table.Name).Select(table.IndexOf).Where(i => i >= 0)];

    /// <summary>
    /// The display columns that make a name in <paramref name="table"/>, in order: those it has that read as text (WP-65) –
    /// what the lookup reads and what the header badges number.
    /// </summary>
    public IReadOnlyList<int> PartsIn(TableDetails table) =>
        [.. IndexesIn(table).Where(i => NotDisplayableReason(table.Columns[i]) is null)];

    /// <summary>A copy with the table's display columns set, or removed with no <paramref name="columns"/>.</summary>
    public DisplayColumns With(string table, IReadOnlyList<string> columns)
    {
        var copy = new SortedDictionary<string, IReadOnlyList<string>>(_columns, StringComparer.Ordinal);
        var cleaned = Cleaned(columns);
        if (cleaned.Count == 0)
        {
            copy.Remove(table);
        }
        else
        {
            copy[table] = cleaned;
        }

        return new DisplayColumns(copy);
    }

    /// <summary>A copy with <paramref name="column"/> appended to the table's display columns (unchanged if it is one already).</summary>
    public DisplayColumns Adding(string table, string column) => With(table, [.. ColumnsOf(table), column]);

    /// <summary>A copy without <paramref name="column"/> among the table's display columns.</summary>
    public DisplayColumns Removing(string table, string column) => With(table, [.. ColumnsOf(table).Where(c => c != column)]);

    /// <summary>The same tables with the same columns in the same order (to skip a reload that changed nothing).</summary>
    public bool SameAs(DisplayColumns other) =>
        _columns.Count == other._columns.Count
        && _columns.All(c => other._columns.TryGetValue(c.Key, out var columns) && columns.SequenceEqual(c.Value));

    /// <summary>
    /// The columns of a file: <paramref name="names"/> (WP-65, several per table) win over <paramref name="single"/> (WP-31,
    /// which then holds the first of them for older versions).
    /// </summary>
    internal static DisplayColumns From(IReadOnlyDictionary<string, string>? single, IReadOnlyDictionary<string, List<string>>? names = null)
    {
        var sorted = new SortedDictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var (table, column) in single ?? new Dictionary<string, string>())
        {
            if (!string.IsNullOrWhiteSpace(table) && Cleaned([column]) is { Count: > 0 } cleaned)
            {
                sorted[table] = cleaned;
            }
        }

        foreach (var (table, columns) in names ?? new Dictionary<string, List<string>>())
        {
            if (!string.IsNullOrWhiteSpace(table) && Cleaned(columns ?? []) is { Count: > 0 } cleaned)
            {
                sorted[table] = cleaned;
            }
        }

        return new DisplayColumns(sorted);
    }

    /// <summary>Without blank names and repeats, in order.</summary>
    private static List<string> Cleaned(IEnumerable<string?> columns) =>
        [.. columns.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c!).Distinct(StringComparer.Ordinal)];

    /// <summary>
    /// Whether a column can be a display column: its value must read as a short text. Not LOBs, LONG, RAW or types that
    /// are only shown as "NULL or not". Null if it can; otherwise why not.
    /// </summary>
    public static string? NotDisplayableReason(ColumnInfo column) => ColumnCategories.Of(column) switch
    {
        ColumnCategory.Clob or ColumnCategory.Blob or ColumnCategory.Long or ColumnCategory.Raw or ColumnCategory.Unsupported =>
            TextFormat.Format(DisplayText.ColumnNotDisplayable, column.DisplayType),
        _ => null,
    };
}

/// <summary>
/// The file of the display columns as it is written: a version and one entry per table, sorted, so it diffs and merges
/// cleanly in Git. Properties a newer FerretSharp adds are kept when this one writes (<see cref="Other"/>).
/// </summary>
internal sealed class DisplayColumnDocument
{
    /// <summary>The version this FerretSharp writes when a table has several display columns (WP-65).</summary>
    public const int CurrentVersion = 2;

    /// <summary>The version of a file without <see cref="DisplayNames"/>: older versions keep reading and writing it.</summary>
    public const int SingleColumnVersion = 1;

    public int Version { get; set; } = SingleColumnVersion;

    /// <summary>One display column per table (WP-31); for a table in <see cref="DisplayNames"/> its first column.</summary>
    public SortedDictionary<string, string> DisplayColumns { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Tables with several display columns, in order (WP-65); null if there are none. Older versions keep it unread
    /// (<see cref="Other"/>) – the version keeps them from writing.
    /// </summary>
    public SortedDictionary<string, List<string>>? DisplayNames { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Other { get; set; }

    public static readonly JsonSerializerOptions Options = JsonFiles.Options;

    /// <summary>The columns read from the document.</summary>
    public Display.DisplayColumns Columns() => Display.DisplayColumns.From(DisplayColumns, DisplayNames);

    /// <summary>
    /// Writes <paramref name="columns"/> into the document: one column per table into <see cref="DisplayColumns"/>, several
    /// into <see cref="DisplayNames"/> (and their first into <see cref="DisplayColumns"/>), the version as low as the
    /// content allows.
    /// </summary>
    public void Set(Display.DisplayColumns columns)
    {
        DisplayColumns = new(columns.All.ToDictionary(c => c.Key, c => c.Value[0]), StringComparer.Ordinal);
        var several = columns.All.Where(c => c.Value.Count > 1).ToDictionary(c => c.Key, c => c.Value.ToList());
        DisplayNames = several.Count > 0 ? new(several, StringComparer.Ordinal) : null;
        Version = DisplayNames is null ? SingleColumnVersion : CurrentVersion;
    }
}
