using FerretSharp.Core.Data;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;
using FerretSharp.UI.Resources;
using FerretSharp.UI.State;
using NSubstitute;

namespace FerretSharp.UI.Tests.State;

/// <summary>WP-58: marked cells copied as shown with rows not loaded read first, and whole columns read again.</summary>
public sealed class CellCopiesTests
{
    private static ColumnInfo Col(string name, string type) => new(name, type, null, false, null, null, true, false, null, 0);

    private static readonly TableDetails Kunden = new(
        new TableSummary("APP", "KUNDEN", TableKind.Table),
        [Col("ID", "NUMBER"), Col("NAME", "VARCHAR2"), Col("ART", "NUMBER"), Col("NOTIZ", "CLOB")], ["ID"], [], false);

    private readonly ShellState _shell = new();
    private readonly IClipboardService _clipboard = Substitute.For<IClipboardService>();
    private readonly List<string> _copied = [];
    private readonly List<(int Start, int Count)> _reads = [];

    /// <summary>The rows the grid has loaded, by grid index; the others are "in the database".</summary>
    private readonly Dictionary<int, RowData> _loaded = [];

    public CellCopiesTests()
    {
        _clipboard.SetTextAsync(default!).ReturnsForAnyArgs(call =>
        {
            _copied.Add(call.Arg<string>());
            return true;
        });
    }

    private static RowData Row(int id) => new(RowKey.None.Instance, [(decimal)id, $"N{id}", id % 2 == 0 ? 2m : 7m, null]);

    /// <summary>ART as the C# model shows it: an enum member for 2.</summary>
    private static ExportColumn ColumnOf(int column) => column == 2
        ? new ExportColumn("Art", 2, Kunden.Columns[2]) { Present = v => v is 2m ? "Gewerbe (2)" : v }
        : new ExportColumn(Kunden.Columns[column].Name, column, Kunden.Columns[column]);

    private MarkedCells Grid(Func<IReadOnlyList<int>, AllColumnValues>? all = null, int pending = 0, int rowsInDatabase = int.MaxValue) => new(
        ColumnOf,
        i => _loaded.GetValueOrDefault(i),
        (start, count) =>
        {
            _reads.Add((start, count));
            return Task.FromResult<IReadOnlyList<RowData>?>([.. Enumerable.Range(start, Math.Max(0, Math.Min(count, rowsInDatabase - start))).Select(Row)]);
        },
        all,
        _ => pending);

    private Task<bool> CopyAsync(string text)
    {
        _copied.Add(text);
        return Task.FromResult(true);
    }

    private Task CopyAsync(MarkedCells grid, GridMarks marks, MarksCopy kind) =>
        CellCopies.CopyAsync(_shell, CopyAsync, _clipboard, new ListLogger(), null, grid, marks, kind);

    private void Load(int from, int to)
    {
        for (var i = from; i <= to; i++)
        {
            _loaded[i] = Row(i);
        }
    }

    [Fact]
    public async Task A_range_copies_its_cells_as_shown_in_display_order()
    {
        Load(0, 9);

        await CopyAsync(Grid(), new GridMarks(2, 3, ["c2", "c1"], false), MarksCopy.Cells);

        Assert.Equal(["Gewerbe (2)\tN2\r\n7\tN3"], _copied);
        Assert.Empty(_reads);
        Assert.Equal("2 × 2 Zellen kopiert.", _shell.Notice!.Text);
    }

    [Fact]
    public async Task As_a_table_the_range_gets_its_headers()
    {
        Load(0, 9);

        await CopyAsync(Grid(), new GridMarks(1, 1, ["c0", "c2"], false), MarksCopy.Table);

        Assert.Equal(["ID\tArt\r\n1\t7"], _copied);
        Assert.Equal("1 × 2 Zellen als Tabelle kopiert.", _shell.Notice!.Text);
    }

    [Fact]
    public async Task Rows_of_a_range_that_are_not_loaded_are_read_block_by_block()
    {
        Load(0, 499);
        Load(1500, 1999); // dropped from the cache in between, or never loaded

        await CopyAsync(Grid(), new GridMarks(498, 1501, ["c0"], false), MarksCopy.Cells);

        Assert.Equal([(500, 500), (1000, 500)], _reads);
        var lines = _copied.Single().Split("\r\n");
        Assert.Equal(1004, lines.Length);
        Assert.Equal(Enumerable.Range(498, 1004).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)), lines);
    }

    [Fact]
    public async Task A_read_that_ends_early_ends_the_range_there()
    {
        await CopyAsync(Grid(rowsInDatabase: 3), new GridMarks(0, 9, ["c0"], false), MarksCopy.Cells);

        Assert.Equal(["0\r\n1\r\n2"], _copied);
    }

    [Fact]
    public async Task A_read_that_failed_copies_nothing()
    {
        var grid = Grid() with { ReadRows = (_, _) => Task.FromResult<IReadOnlyList<RowData>?>(null) };

        await CopyAsync(grid, new GridMarks(0, 9, ["c0"], false), MarksCopy.Cells);

        Assert.Empty(_copied);
    }

    [Fact]
    public async Task A_range_with_too_many_cells_reads_and_copies_nothing()
    {
        await CopyAsync(Grid(), new GridMarks(0, ColumnValues.MaxValues / 2, ["c0", "c1"], false), MarksCopy.Cells);

        Assert.Empty(_reads);
        Assert.Empty(_copied);
        Assert.Matches("^Mehr als 100.000 Zellen – nichts kopiert", _shell.Notice!.Text);
    }

    [Fact]
    public async Task A_range_of_one_column_gives_an_in_list_of_database_values_but_several_columns_none()
    {
        Load(0, 9);

        await CopyAsync(Grid(), new GridMarks(0, 3, ["c1", "c2"], false), MarksCopy.InList);
        Assert.Empty(_copied);

        await CopyAsync(Grid(), new GridMarks(0, 3, ["c2"], false), MarksCopy.InList);
        Assert.Equal(["2, 7"], _copied);
    }

    /// <summary>All values of the wanted columns in one block, like the table's query over these columns.</summary>
    private static AllColumnValues All(IReadOnlyList<int> columns, params RowData[] rows) =>
        new("Alle Zeilen des Filters", rows.Length,
            _ => RowExport.FromRows([.. rows.Select(r => new RowData(RowKey.None.Instance, [.. columns.Select(c => r.Values[c])]))]),
            [.. Enumerable.Range(0, columns.Count)], "", Guid.NewGuid());

    [Fact]
    public async Task Whole_columns_are_read_again_and_copied_through_the_host_with_a_note_on_pending_changes()
    {
        IReadOnlyList<int>? asked = null;
        var grid = Grid(columns =>
        {
            asked = columns;
            return All(columns, Row(1), Row(2));
        }, pending: 2);

        await CopyAsync(grid, new GridMarks(-1, -1, ["c2", "c1"], true), MarksCopy.Table);

        Assert.Equal([2, 1], asked);
        Assert.Equal(["Art\tNAME\r\n7\tN1\r\nGewerbe (2)\tN2"], _copied);
        Assert.Equal("2 Spalten · alle Zeilen als Tabelle kopiert: 2 Zeilen.", _shell.Notice!.Text);
        Assert.Equal(["Stand der Datenbank: 2 Zeilen mit ausstehenden Änderungen sind nicht wie angezeigt enthalten – vorher schreiben."], _shell.Notice.Warnings);
        Assert.Empty(_shell.BackgroundReads.Running);
    }

    [Fact]
    public async Task A_whole_column_gives_an_in_list_and_lob_columns_are_refused()
    {
        await CopyAsync(Grid(columns => All(columns, Row(1), Row(2), Row(3))), new GridMarks(-1, -1, ["c2"], true), MarksCopy.InList);
        Assert.Equal(["7, 2"], _copied); // each value once, in the order read

        await CopyAsync(Grid(columns => throw new InvalidOperationException("not read")), new GridMarks(-1, -1, ["c1", "c3"], true), MarksCopy.Cells);
        Assert.Single(_copied);
        Assert.Equal(GridText.GridMenu_CopyColumnUnavailable, _shell.Notice!.Text);
    }

    [Fact]
    public async Task Without_a_read_of_all_rows_whole_columns_copy_nothing()
    {
        await CopyAsync(Grid(all: null), new GridMarks(-1, -1, ["c1"], true), MarksCopy.Cells);

        Assert.Empty(_copied);
    }

    [Fact]
    public async Task A_loaded_result_gives_its_values_without_running_the_query()
    {
        var all = ColumnValueCopies.ForLoaded([Row(1), Row(2)], [2, 1], Guid.NewGuid());

        await CopyAsync(Grid(_ => all), new GridMarks(-1, -1, ["c2", "c1"], true), MarksCopy.Cells);

        Assert.Equal(["7\tN1\r\nGewerbe (2)\tN2"], _copied);
        Assert.Equal(2, all.Total);
    }
}
