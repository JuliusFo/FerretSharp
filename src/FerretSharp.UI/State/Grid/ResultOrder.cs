using FerretSharp.Core.Data;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;
using FerretSharp.UI.Resources;

namespace FerretSharp.UI.State;

/// <summary>
/// How a query result is sorted by its headers (SQL editor, LINQ console). Once every row is loaded in the query's own
/// order, a header click sorts them in memory (<see cref="RowOrder"/>) – no read, the snapshot stays; before that, the
/// database sorts (<see cref="SqlResultOrder"/>) and the grid reads the blocks again. A column sorts if its type does and
/// one of the two can sort it.
/// </summary>
public sealed class ResultOrder
{
    private readonly QuerySpec _query;
    private readonly IReadOnlyList<SqlColumn> _columns;
    private readonly bool _byDatabase;

    /// <summary>Every row in the query's own order, once they were all loaded unsorted (and still kept).</summary>
    private IReadOnlyList<RowData>? _all;

    public ResultOrder(QuerySpec query, IReadOnlyList<SqlColumn> columns)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(columns);
        _query = query;
        _columns = columns;
        _byDatabase = columns.Count > 0 && SqlResultOrder.CanSort(query, columns);
    }

    /// <summary>The order the headers ask for; empty for the query's own.</summary>
    public IReadOnlyList<ResultSort> Sorts { get; private set; } = [];

    /// <summary>All rows in the current order if they are known in memory; null if the grid reads them.</summary>
    public IReadOnlyList<RowData>? Ordered { get; private set; }

    /// <summary>The query to read rows in the current order – also for copying whole columns and exporting all rows.</summary>
    public QuerySpec Query => SqlResultOrder.Sorted(_query, _columns, Sorts) ?? _query;

    /// <summary>The header of this result column sorts.</summary>
    public bool Sortable(int column) =>
        column < _columns.Count && ColumnCategories.IsSortable(ColumnCategories.Of(_columns[column].Column))
        && (_byDatabase || (_all is not null && RowOrder.CanSort(_columns[column].Column)));

    /// <summary>
    /// Why a header of a sortable type does not sort yet: the database cannot sort the query, the rows are not all loaded
    /// (<see cref="GridColumn.SortHint"/>); null otherwise – a LOB says so by its type.
    /// </summary>
    public string? HintOf(int column) =>
        column < _columns.Count && !Sortable(column) && ColumnCategories.IsSortable(ColumnCategories.Of(_columns[column].Column))
            ? RowOrder.CanSort(_columns[column].Column) ? SqlEditorText.Result_SortWhenLoaded : SqlEditorText.Result_SortNotPossible
            : null;

    /// <summary>AG Grid's sort model as result columns (ids <c>c0</c>, <c>c1</c> …, in the order of the sort indexes).</summary>
    public static IReadOnlyList<ResultSort> Of(IReadOnlyList<GridSort> sortModel)
    {
        ArgumentNullException.ThrowIfNull(sortModel);
        return [.. sortModel.Select(s => new ResultSort(GridColumn.IndexOf(s.ColId), s.Sort == "desc"))];
    }

    /// <summary>The headers ask for <paramref name="sorts"/>: true if that is another order (the kept blocks are stale).</summary>
    public bool Apply(IReadOnlyList<ResultSort> sorts)
    {
        ArgumentNullException.ThrowIfNull(sorts);
        if (sorts.SequenceEqual(Sorts))
        {
            return false;
        }

        Sorts = sorts;
        Ordered = sorts.Count == 0 ? _all
            : _all is not null && sorts.All(s => RowOrder.CanSort(_columns[s.Column].Column)) ? RowOrder.Sort(_all, sorts)
            : null;
        return true;
    }

    /// <summary>
    /// Every row was read in the query's own order: from now on they sort in memory. True if headers sort now that did not
    /// before (the database cannot sort the query) – the grid relabels them. Ignored while sorted or once known.
    /// </summary>
    public bool Loaded(IReadOnlyList<RowData> all)
    {
        ArgumentNullException.ThrowIfNull(all);
        if (Sorts.Count > 0 || _all is not null)
        {
            return false;
        }

        _all = all;
        Ordered = all;
        return !_byDatabase && _columns.Any(c => RowOrder.CanSort(c.Column) && ColumnCategories.IsSortable(ColumnCategories.Of(c.Column)));
    }
}
