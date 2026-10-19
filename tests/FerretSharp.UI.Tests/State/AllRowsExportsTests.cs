using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Data;
using FerretSharp.Core.Display;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;
using FerretSharp.UI.State;
using NSubstitute;

namespace FerretSharp.UI.Tests.State;

/// <summary>WP-54: what "all rows" of a table tab and of a SQL result read, and the Excel columns they write.</summary>
public sealed class AllRowsExportsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ColumnInfo Col(string name, string type) => new(name, type, null, false, null, null, true, false, null, 0);

    private static readonly TableDetails Kunden = new(
        new TableSummary("APP", "KUNDEN", TableKind.Table), [Col("ID", "NUMBER"), Col("NAME", "VARCHAR2"), Col("MANDANT_ID", "NUMBER")], ["ID"], [], false);

    private static readonly TableDetails Mandant = new(
        new TableSummary("APP", "MANDANT", TableKind.Table), [Col("ID", "NUMBER"), Col("NAME", "VARCHAR2")], ["ID"], [], false);

    private static readonly DisplayLink Link = new(2,
        new ForeignKeyInfo("FK_MANDANT", Kunden.Table.Ref, ["MANDANT_ID"], Mandant.Table.Ref, ["ID"], FkSource.Declared), Mandant, 0, 1);

    private readonly IDataAccess _data = Substitute.For<IDataAccess>();
    private readonly List<Guid> _workspaces = [];

    private Task<IDataAccess> DataOf(Guid workspace, CancellationToken cancellationToken)
    {
        _workspaces.Add(workspace);
        return Task.FromResult(_data);
    }

    private static Task<bool> Ignore(IReadOnlyList<RowData> rows, IRowLookup lookup, CancellationToken cancellationToken) => Task.FromResult(true);

    [Fact]
    public async Task A_table_reads_the_applied_filters_and_sort_order_as_they_were_when_the_menu_opened()
    {
        var tab = new TableTab(Guid.NewGuid(), Kunden.Table) { TotalCount = 1234 };
        IReadOnlyList<FilterCondition> filters = [FilterCondition.Of("NAME", FilterOperator.Contains, "GmbH")];
        IReadOnlyList<SortSpec> sorts = [new SortSpec("NAME", true)];
        tab.AppliedFilters = filters;
        tab.Sorts = sorts;

        var all = AllRowsExports.ForTable(tab, Kunden, TablePresentation.Plain(Kunden), DisplayNames.None, TablePresentation.Plain, DataOf);
        tab.AppliedFilters = []; // the user filters again while the menu is open
        await all.Read(Ignore, Ct);

        await _data.Received(1).ReadAllAsync(Kunden, filters, sorts, Arg.Any<RowBlockHandler>(), Ct);
        Assert.Equal([tab.WorkspaceId], _workspaces);
        Assert.Equal(1234, all.Total);
        Assert.Equal("Alle Zeilen des Filters · 1.234 Zeilen", all.Heading);
    }

    [Fact]
    public void Without_a_count_the_total_is_known_only_once_all_rows_are_loaded()
    {
        var tab = new TableTab(Guid.NewGuid(), Kunden.Table) { RowsLoaded = 500 };

        var open = AllRowsExports.ForTable(tab, Kunden, TablePresentation.Plain(Kunden), DisplayNames.None, TablePresentation.Plain, DataOf);
        tab.AllRowsLoaded = true;
        var loaded = AllRowsExports.ForTable(tab, Kunden, TablePresentation.Plain(Kunden), DisplayNames.None, TablePresentation.Plain, DataOf);

        Assert.Null(open.Total);
        Assert.Equal("Alle Zeilen des Filters", open.Heading);
        Assert.Equal(500, loaded.Total);
    }

    [Fact]
    public void The_excel_columns_look_up_names_of_their_own_with_every_link_of_the_grid()
    {
        var gridNames = new DisplayNames([Link], (_, value) => value as string);
        var lookup = Assert.Single(gridNames.Missing([[1m, "A", 7m]]));
        gridNames.Failed(lookup, "ORA-00942"); // switched off in the grid
        var tab = new TableTab(Guid.NewGuid(), Kunden.Table);
        var asked = new List<TableRef>();

        var all = AllRowsExports.ForTable(tab, Kunden, TablePresentation.Plain(Kunden), gridNames, t =>
        {
            asked.Add(t.Table.Ref);
            return TablePresentation.Plain(t);
        }, DataOf);
        Assert.Empty(asked); // nothing built while the menu only opens

        var excel = all.Excel();

        Assert.NotNull(excel.Names);
        Assert.NotSame(gridNames, excel.Names);
        Assert.True(excel.Names.Shows(2));
        Assert.Equal(["ID", "NAME", "MANDANT_ID", "MANDANT_ID · NAME"], excel.Columns.Select(c => c.Header));
        Assert.Equal([Mandant.Table.Ref], asked);
    }

    [Fact]
    public void Without_links_the_excel_columns_need_no_names()
    {
        var all = AllRowsExports.ForTable(new TableTab(Guid.NewGuid(), Kunden.Table), Kunden, TablePresentation.Plain(Kunden), DisplayNames.None,
            TablePresentation.Plain, DataOf);

        var excel = all.Excel();

        Assert.Null(excel.Names);
        Assert.Equal(["ID", "NAME", "MANDANT_ID"], excel.Columns.Select(c => c.Header));
    }

    [Fact]
    public async Task A_result_runs_its_query_again_with_its_column_names()
    {
        var query = new QuerySpec("SELECT ID, NAME FROM KUNDEN", []);
        var columns = new[] { new SqlColumn("ID", Col("ID", "NUMBER")), new SqlColumn("NAME", Col("NAME", "VARCHAR2")) };
        var xlsx = ExportColumn.Raw(Kunden);
        var workspace = Guid.NewGuid();

        var all = AllRowsExports.ForResult(query, columns, 42, xlsx, workspace, DataOf);
        await all.Read(Ignore, Ct);

        await _data.Received(1).ReadSqlAllAsync(query, Arg.Is<IReadOnlyList<string>>(n => n.SequenceEqual(new[] { "ID", "NAME" })), Arg.Any<RowBlockHandler>(), Ct);
        Assert.Equal([workspace], _workspaces);
        Assert.Equal("Alle Zeilen des Ergebnisses · 42 Zeilen", all.Heading);
        Assert.Same(xlsx, all.Excel().Columns);
    }
}
