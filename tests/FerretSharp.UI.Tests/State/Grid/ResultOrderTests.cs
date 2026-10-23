using FerretSharp.Core.Data;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;
using FerretSharp.UI.Resources;
using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>A query result sorted by its headers: in memory once every row is loaded, else by the database.</summary>
public sealed class ResultOrderTests
{
    private static readonly IReadOnlyList<SqlColumn> Columns =
    [
        new("ID", Col("ID", "NUMBER")),
        new("NAME", Col("NAME", "VARCHAR2")),
        new("NOTE", Col("NOTE", "CLOB")),
        new("DAUER", Col("DAUER", "INTERVAL DAY(2) TO SECOND(6)")),
    ];

    private static readonly IReadOnlyList<SqlColumn> SameNames = [new("ID", Col("ID", "NUMBER")), new("ID", Col("ID", "NUMBER")), Columns[3]];

    private static readonly QuerySpec Plain = new("SELECT id, name, note, dauer FROM t", []);

    private static readonly QuerySpec Limited = new("SELECT a.id, b.id, a.dauer FROM a, b FETCH FIRST 9 ROWS ONLY", []);

    private static readonly IReadOnlyList<RowData> Rows = [Row(1m, "b"), Row(2m, "a"), Row(3m, null)];

    private static ColumnInfo Col(string name, string type) => new(name, type, null, true, null, null, false, false, null, 1);

    private static RowData Row(object? id, object? name) => new(RowKey.None.Instance, [id, name, null, null]);

    [Fact]
    public void The_sort_model_becomes_result_columns()
    {
        Assert.Equal([new ResultSort(1, true), new ResultSort(0, false)], ResultOrder.Of([new GridSort("c1", "desc"), new GridSort("c0", "asc")]));
    }

    [Fact]
    public void Before_all_rows_are_loaded_the_database_sorts()
    {
        var order = new ResultOrder(Plain, Columns);

        Assert.True(order.Apply([new ResultSort(1, false)]));

        Assert.Null(order.Ordered);
        Assert.Equal("SELECT id, name, note, dauer FROM t\nORDER BY 2", order.Query.Sql);
        Assert.False(order.Apply([new ResultSort(1, false)])); // the same order again: the kept blocks stay
    }

    [Fact]
    public void Once_all_rows_are_loaded_they_sort_in_memory_and_go_back_to_the_query_s_order()
    {
        var order = new ResultOrder(Plain, Columns);
        Assert.False(order.Loaded(Rows)); // no header sorts that did not before: the database could sort them

        order.Apply([new ResultSort(1, false)]);
        Assert.Equal([Rows[1], Rows[0], Rows[2]], order.Ordered);

        order.Apply([]);
        Assert.Same(Rows, order.Ordered);
        Assert.Same(Plain, order.Query);
    }

    /// <summary>Rows read while sorted are not the query's own order: they do not stand in for it.</summary>
    [Fact]
    public void Rows_loaded_while_sorted_are_not_kept_as_the_query_s_order()
    {
        var order = new ResultOrder(Plain, Columns);
        order.Apply([new ResultSort(0, true)]);

        Assert.False(order.Loaded(Rows));
        order.Apply([]);

        Assert.Null(order.Ordered);
    }

    [Fact]
    public void Lobs_never_sort_an_interval_only_by_the_database()
    {
        var order = new ResultOrder(Plain, Columns);
        order.Loaded(Rows);

        Assert.True(order.Sortable(0));
        Assert.False(order.Sortable(2));
        Assert.Null(order.HintOf(2)); // a LOB says so by its type
        Assert.True(order.Sortable(3));

        order.Apply([new ResultSort(3, false)]);
        Assert.Null(order.Ordered); // read with ORDER BY 4
        Assert.EndsWith("ORDER BY 4", order.Query.Sql, StringComparison.Ordinal);
    }

    /// <summary>ORA-00918 rules the database out: the headers sort once every row is there – and say so to the grid.</summary>
    [Fact]
    public void A_query_the_database_cannot_sort_sorts_once_loaded()
    {
        var order = new ResultOrder(Limited, SameNames);
        Assert.False(order.Sortable(0));
        Assert.Equal(SqlEditorText.Result_SortWhenLoaded, order.HintOf(0));
        Assert.Equal(SqlEditorText.Result_SortNotPossible, order.HintOf(2));

        Assert.True(order.Loaded([Row(2m, 1m), Row(1m, 2m)]));

        Assert.True(order.Sortable(0));
        Assert.Null(order.HintOf(0));
        Assert.False(order.Sortable(2)); // an interval compares only in the database
        Assert.Equal(SqlEditorText.Result_SortNotPossible, order.HintOf(2));
        Assert.Same(Limited, order.Query);
    }
}
