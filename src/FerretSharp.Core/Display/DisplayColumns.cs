using System.Text.Json;
using System.Text.Json.Serialization;
using FerretSharp.Core.IO;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Display;

/// <summary>
/// The display column of each table (WP-31): its value stands beside every foreign key to the table
/// (<c>4711 · PetersPizza</c>). Keyed by table name without owner – the schema is the same on Prod, Test and Dev, the
/// schema name may differ (the user's decision).
/// </summary>
public sealed class DisplayColumns
{
    public static readonly DisplayColumns Empty = new(new SortedDictionary<string, string>(StringComparer.Ordinal));

    private readonly SortedDictionary<string, string> _columns;

    private DisplayColumns(SortedDictionary<string, string> columns) => _columns = columns;

    /// <summary>Table name → column name, sorted by table name (as written to the file).</summary>
    public IReadOnlyDictionary<string, string> All => _columns;

    public int Count => _columns.Count;

    /// <summary>The display column of a table by its name; null without one.</summary>
    public string? ColumnOf(string table) => _columns.GetValueOrDefault(table);

    /// <summary>The display column of a table, if the table has that column (an entry for another database is ignored).</summary>
    public int? IndexIn(TableDetails table) =>
        ColumnOf(table.Table.Name) is { } column && table.IndexOf(column) is var index and >= 0 ? index : null;

    /// <summary>A copy with the table's display column set, or removed with <paramref name="column"/> null.</summary>
    public DisplayColumns With(string table, string? column)
    {
        var columns = new SortedDictionary<string, string>(_columns, StringComparer.Ordinal);
        if (column is null)
        {
            columns.Remove(table);
        }
        else
        {
            columns[table] = column;
        }

        return new DisplayColumns(columns);
    }

    /// <summary>The same tables with the same columns (to skip a reload that changed nothing).</summary>
    public bool SameAs(DisplayColumns other) =>
        _columns.Count == other._columns.Count && _columns.All(c => other._columns.TryGetValue(c.Key, out var column) && column == c.Value);

    internal static DisplayColumns From(IReadOnlyDictionary<string, string>? columns)
    {
        var sorted = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (table, column) in columns ?? new Dictionary<string, string>())
        {
            if (!string.IsNullOrWhiteSpace(table) && !string.IsNullOrWhiteSpace(column))
            {
                sorted[table] = column;
            }
        }

        return new DisplayColumns(sorted);
    }

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
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    public SortedDictionary<string, string> DisplayColumns { get; set; } = new(StringComparer.Ordinal);

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Other { get; set; }

    public static readonly JsonSerializerOptions Options = JsonFiles.Options;
}
