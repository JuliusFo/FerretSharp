using FerretSharp.Core.Oracle;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Data;

/// <summary>How a row differs between the start of a data diff and now (WP-44).</summary>
public enum DiffStatus
{
    /// <summary>Only now: inserted since, or moved into the filter.</summary>
    Added,

    /// <summary>Values differ in at least one compared column.</summary>
    Changed,

    /// <summary>Only at the start: deleted since, or moved out of the filter.</summary>
    Deleted,
}

/// <summary>A row of the diff query as read: its side (<see cref="QueryBuilder.DiffSideNow"/>/<see cref="QueryBuilder.DiffSideThen"/>) and values.</summary>
public sealed record DiffSideRow(bool Now, RowData Row);

/// <summary>What <see cref="IDataAccess.ReadDiffAsync"/> read for one table.</summary>
/// <param name="Rows">Ordered by row key, then side – the two halves of a changed row follow each other.</param>
/// <param name="More">The database had more rows than were read.</param>
/// <param name="Omitted">Indexes of columns not compared (LONG, XMLTYPE, object types …).</param>
/// <param name="RowMovement">Rows are keyed by ROWID and the table allows row movement: a moved row looks deleted and added.</param>
public sealed record DiffRead(IReadOnlyList<DiffSideRow> Rows, bool More, IReadOnlySet<int> Omitted, RowKeyKind RowKey, bool RowMovement);

/// <summary>A row that differs, with its values at the start (<see cref="Then"/>) and now (<see cref="Now"/>).</summary>
/// <param name="Changed">Columns whose values differ (only for <see cref="DiffStatus.Changed"/>).</param>
public sealed record DiffRow(DiffStatus Status, RowKey Key, RowData? Then, RowData? Now, IReadOnlySet<int> Changed)
{
    /// <summary>The values to show: now, or at the start for a deleted row.</summary>
    public RowData Shown => Now ?? Then!;
}

/// <summary>The data diff of one table (WP-44).</summary>
/// <param name="Truncated">There were more than <see cref="DataDiff.MaxRows"/> differing rows; only the first are here.</param>
public sealed record TableDiff(
    TableDetails Table, IReadOnlyList<FilterCondition> Filters, IReadOnlyList<DiffRow> Rows, bool Truncated, IReadOnlySet<int> Omitted,
    RowKeyKind RowKey, bool RowMovement)
{
    public int Count(DiffStatus status) => Rows.Count(r => r.Status == status);
}

/// <summary>
/// Pairs the rows of a diff query (WP-44): a key on one side only is a new or deleted row, on both a changed one – its cells
/// are compared here, on the values as read (<see cref="OracleTypeMapper.ValuesEqual"/>, as the row comparison does).
/// </summary>
public static class DataDiff
{
    /// <summary>Differing rows of a table at most (decision of the user, 2026-11-08); more are cut with a notice.</summary>
    public const int MaxRows = 10_000;

    /// <summary>
    /// Rows of the diff query to read at most for <see cref="MaxRows"/> differing rows: two per changed row, and one more to
    /// know a changed row was not cut in half.
    /// </summary>
    public const int ReadLimit = (2 * MaxRows) + 1;

    public static TableDiff Pair(TableDetails table, IReadOnlyList<FilterCondition> filters, DiffRead read)
    {
        ArgumentNullException.ThrowIfNull(read);
        var rows = new List<DiffRow>();
        var sides = read.Rows;
        var truncated = read.More;
        var compared = Enumerable.Range(0, table.Columns.Count).Where(i => !read.Omitted.Contains(i)).ToList();
        for (var i = 0; i < sides.Count;)
        {
            var key = sides[i].Row.Key;
            var end = i + 1;
            while (end < sides.Count && sides[end].Row.Key.Equals(key))
            {
                end++;
            }

            // The last key of a cut read may miss its other half: left out, the read counts as cut.
            if (read.More && end == sides.Count)
            {
                break;
            }

            if (rows.Count == MaxRows)
            {
                truncated = true;
                break;
            }

            var then = sides.Skip(i).Take(end - i).FirstOrDefault(s => !s.Now)?.Row;
            var now = sides.Skip(i).Take(end - i).FirstOrDefault(s => s.Now)?.Row;
            rows.Add((then, now) switch
            {
                (null, { } added) => new DiffRow(DiffStatus.Added, key, null, added, Empty),
                ({ } deleted, null) => new DiffRow(DiffStatus.Deleted, key, deleted, null, Empty),
                _ => new DiffRow(DiffStatus.Changed, key, then, now,
                    compared.Where(c => !OracleTypeMapper.ValuesEqual(then!.Values[c], now!.Values[c])).ToHashSet()),
            });
            i = end;
        }

        return new TableDiff(table, filters, rows, truncated, read.Omitted, read.RowKey, read.RowMovement);
    }

    private static readonly IReadOnlySet<int> Empty = new HashSet<int>();
}
