using FerretSharp.Core.Data;
using FerretSharp.Core.Query;
using FerretSharp.Core.Workspaces;

namespace FerretSharp.UI.State;

/// <summary>What the data diff tab (WP-44) shows of a table's diff: which columns, which rows.</summary>
public static class DataDiffRows
{
    /// <summary>
    /// The columns in the grid's display order (key first), without those a set operation could not compare. With
    /// <paramref name="onlyChanged"/> only the key and the columns that differ in a changed row.
    /// </summary>
    public static IReadOnlyList<int> Columns(TableDiff diff, bool onlyChanged)
    {
        ArgumentNullException.ThrowIfNull(diff);
        var key = diff.Table.PrimaryKey.Select(diff.Table.IndexOf).ToHashSet();
        var changed = diff.Rows.SelectMany(r => r.Changed).ToHashSet();
        return ColumnPinning.DisplayOrder(diff.Table, [])
            .Where(c => !diff.Omitted.Contains(c) && (!onlyChanged || key.Contains(c) || changed.Contains(c)))
            .ToList();
    }

    /// <summary>The rows of one status, or all (null) in the order read: by key.</summary>
    public static IReadOnlyList<DiffRow> Rows(TableDiff diff, DiffStatus? status) =>
        status is { } only ? diff.Rows.Where(r => r.Status == only).ToList() : diff.Rows;

    /// <summary>The names of the columns that were not compared, for the notice.</summary>
    public static IReadOnlyList<string> OmittedNames(TableDiff diff) =>
        diff.Omitted.Order().Select(i => diff.Table.Columns[i].Name).ToList();

    /// <summary>When the start point was set, as short as it is clear: the time today, else date and time.</summary>
    public static string StartText(DateTimeOffset startedAt, DateTimeOffset now)
    {
        var local = startedAt.ToLocalTime();
        return local.ToString(local.Date == now.ToLocalTime().Date ? "t" : "g", System.Globalization.CultureInfo.CurrentCulture);
    }

    /// <summary>Active filter conditions of an entry; 0 compares the whole table.</summary>
    public static int FilterCount(DataDiffEntry entry) => FilterGroups.Active(entry.Filters).Count();
}
