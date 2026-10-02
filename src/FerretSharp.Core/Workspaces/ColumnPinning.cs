using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Workspaces;

/// <summary>
/// Columns pinned to the left edge of the grid. Primary key columns are always pinned (first, in schema order) and
/// are never part of the user's list; the user's columns follow in the order they were pinned.
/// </summary>
public static class ColumnPinning
{
    /// <summary>
    /// The user's pinned columns that still exist in the table, without primary key columns and duplicates
    /// (a saved tab may refer to a column that has since been dropped).
    /// </summary>
    public static IReadOnlyList<string> Normalize(TableDetails table, IEnumerable<string> pinned)
    {
        var columns = table.Columns.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        return pinned.Where(c => columns.Contains(c) && !table.PrimaryKey.Contains(c)).Distinct(StringComparer.Ordinal).ToList();
    }

    public static bool IsPinned(TableDetails table, IReadOnlyList<string> pinned, string column) =>
        table.PrimaryKey.Contains(column) || pinned.Contains(column);

    /// <summary>Pins the column after the already pinned ones; primary key columns stay as they are.</summary>
    public static IReadOnlyList<string> Pin(TableDetails table, IReadOnlyList<string> pinned, string column) =>
        Normalize(table, [.. pinned, column]);

    /// <summary>Releases the column; primary key columns cannot be released.</summary>
    public static IReadOnlyList<string> Unpin(TableDetails table, IReadOnlyList<string> pinned, string column) =>
        Normalize(table, pinned.Where(c => c != column));

    public static IReadOnlyList<string> Toggle(TableDetails table, IReadOnlyList<string> pinned, string column) =>
        IsPinned(table, pinned, column) ? Unpin(table, pinned, column) : Pin(table, pinned, column);

    /// <summary>
    /// Indexes into <see cref="TableDetails.Columns"/> in display order: primary key, then the user's pinned columns,
    /// then the rest in schema order.
    /// </summary>
    public static IReadOnlyList<int> DisplayOrder(TableDetails table, IReadOnlyList<string> pinned)
    {
        var index = table.Columns.Select((c, i) => (c.Name, i)).ToDictionary(x => x.Name, x => x.i, StringComparer.Ordinal);
        var primaryKey = table.Columns.Select((c, i) => (c.Name, i)).Where(x => table.PrimaryKey.Contains(x.Name)).Select(x => x.i);
        var user = Normalize(table, pinned).Select(c => index[c]);
        var front = primaryKey.Concat(user).ToList();
        return [.. front, .. Enumerable.Range(0, table.Columns.Count).Except(front)];
    }
}
