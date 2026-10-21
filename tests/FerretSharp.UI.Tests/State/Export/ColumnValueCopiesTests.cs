using FerretSharp.Core.Connections;
using FerretSharp.Core.Data;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;
using FerretSharp.UI.Resources;
using FerretSharp.UI.State;
using NSubstitute;

namespace FerretSharp.UI.Tests.State;

/// <summary>WP-57: the values of a column – from the selected rows, or all of them read again in the background.</summary>
public sealed class ColumnValueCopiesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ColumnInfo Col(string name, string type) => new(name, type, null, false, null, null, true, false, null, 0);

    private static readonly TableDetails Kunden = new(
        new TableSummary("APP", "KUNDEN", TableKind.Table), [Col("ID", "NUMBER"), Col("NAME", "VARCHAR2"), Col("ART", "NUMBER")], ["ID"], [], false);

    /// <summary>ART as the C# model shows it: an enum member for 2.</summary>
    private static readonly ExportColumn Art = new("Art", 2, Kunden.Columns[2]) { Present = v => v is 2m ? "Gewerbe (2)" : v };

    private readonly ShellState _shell = new();
    private readonly IDataAccess _data = Substitute.For<IDataAccess>();
    private readonly IClipboardService _clipboard = Substitute.For<IClipboardService>();
    private readonly List<string> _copied = [];

    public ColumnValueCopiesTests()
    {
        _clipboard.SetTextAsync(default!).ReturnsForAnyArgs(call =>
        {
            _copied.Add(call.Arg<string>());
            return true;
        });
    }

    private Task<IDataAccess> DataOf(Guid workspace, CancellationToken cancellationToken) => Task.FromResult(_data);

    private static RowData Row(params object?[] values) => new(RowKey.None.Instance, values);

    private static Task<bool> Ignore(IReadOnlyList<RowData> rows, IRowLookup lookup, CancellationToken cancellationToken) => Task.FromResult(true);

    private Task<bool> CopyAsync(string text)
    {
        _copied.Add(text);
        return Task.FromResult(true);
    }

    /// <summary>All values read in blocks of the given rows (one value per row, like the table's column query).</summary>
    private static AllColumnValues Blocks(Action<bool>? asked, params IReadOnlyList<RowData>[] blocks) =>
        new("Alle Zeilen des Filters", null, distinct => async (onBlock, ct) =>
        {
            asked?.Invoke(distinct);
            foreach (var block in blocks)
            {
                if (!await onBlock(block, Substitute.For<IRowLookup>(), ct))
                {
                    return;
                }
            }
        }, 0, "");

    private Task CopyAllAsync(ColumnValuesFormat format, AllColumnValues all) =>
        ColumnValueCopies.CopyAllAsync(_shell, _clipboard, new ListLogger(), null, Art with { Source = 0 }, "ART", format, all);

    [Fact]
    public async Task A_table_reads_its_column_under_the_filters_applied_when_the_menu_opened()
    {
        var tab = new TableTab(Guid.NewGuid(), Kunden.Table) { TotalCount = 234 };
        IReadOnlyList<FilterCondition> filters = [FilterCondition.Of("NAME", FilterOperator.Contains, "GmbH")];
        IReadOnlyList<SortSpec> sorts = [new SortSpec("NAME", true)];
        tab.AppliedFilters = filters;
        tab.Sorts = sorts;

        var all = ColumnValueCopies.ForTable(tab, Kunden, 2, DataOf);
        tab.AppliedFilters = [];
        await all.Read(false)(Ignore, Ct);
        await all.Read(true)(Ignore, Ct);

        await _data.Received(1).ReadColumnAllAsync(Kunden, 2, filters, sorts, false, Arg.Any<RowBlockHandler>(), Ct);
        await _data.Received(1).ReadColumnAllAsync(Kunden, 2, filters, sorts, true, Arg.Any<RowBlockHandler>(), Ct);
        Assert.Equal(0, all.ValueIndex);
        Assert.True(all.DistinctInQuery);
        Assert.Equal(234, all.Total);
        Assert.Equal("Alle Zeilen des Filters · 234 Zeilen", all.Heading);
    }

    [Fact]
    public async Task A_result_runs_its_query_again_and_takes_the_column_from_each_row()
    {
        var query = new QuerySpec("SELECT ID, NAME FROM KUNDEN", []);
        IReadOnlyList<SqlColumn> columns = [new("ID", Kunden.Columns[0]), new("NAME", Kunden.Columns[1])];

        var all = ColumnValueCopies.ForResult(query, columns, 1, null, Guid.NewGuid(), DataOf);
        await all.Read(true)(Ignore, Ct); // the query itself is not made distinct

        await _data.Received(1).ReadSqlAllAsync(query, Arg.Is<IReadOnlyList<string>>(n => n.SequenceEqual(new[] { "ID", "NAME" })), Arg.Any<RowBlockHandler>(), Ct);
        Assert.Equal(1, all.ValueIndex);
        Assert.False(all.DistinctInQuery);
        Assert.Equal("Alle Zeilen des Ergebnisses", all.Heading);
    }

    [Fact]
    public async Task The_selected_rows_give_their_values_as_shown_and_name_rows_no_longer_loaded()
    {
        await ColumnValueCopies.CopyRowsAsync(_shell, CopyAsync, Art, "ART", ColumnValuesFormat.Lines,
            [Row(1m, "A", 2m), Row(2m, "B", 7m), Row(3m, "C", null)], missing: 1);

        Assert.Equal(["Gewerbe (2)\r\n7\r\n"], _copied);
        Assert.Equal("3 Werte von ART kopiert.", _shell.Notice!.Text);
        Assert.Equal(["1 der markierten Zeilen ist nicht mehr geladen – nicht kopiert."], _shell.Notice.Warnings);
    }

    [Fact]
    public async Task The_selected_rows_give_an_in_list_of_database_values()
    {
        await ColumnValueCopies.CopyRowsAsync(_shell, CopyAsync, Art, "ART", ColumnValuesFormat.InList, [Row(1m, "A", 2m), Row(2m, "B", 2m), Row(3m, "C", null)], 0);

        Assert.Equal(["2"], _copied);
        Assert.Equal("1 Wert von ART als IN-Liste kopiert.", _shell.Notice!.Text);
        Assert.Equal(["1 NULL-Wert steht nicht in der Liste: IN findet NULL nie."], _shell.Notice.Warnings);
    }

    [Fact]
    public async Task Only_null_copies_nothing()
    {
        await ColumnValueCopies.CopyRowsAsync(_shell, CopyAsync, Art, "ART", ColumnValuesFormat.InList, [Row(1m, "A", null)], 0);

        Assert.Empty(_copied);
        Assert.Equal("ART: keine Werte zum Kopieren.", _shell.Notice!.Text);
    }

    [Fact]
    public async Task A_clipboard_that_refuses_says_so()
    {
        await ColumnValueCopies.CopyRowsAsync(_shell, _ => Task.FromResult(false), Art, "ART", ColumnValuesFormat.Lines, [Row(1m, "A", 7m)], 0);

        Assert.Equal(CommonText.ClipboardUnavailable, _shell.Notice!.Text);
    }

    [Fact]
    public async Task All_values_are_read_in_the_background_shown_in_the_status_bar_and_copied_at_the_end()
    {
        FileExport? running = null;
        bool? distinct = null;
        var all = Blocks(d =>
        {
            distinct = d;
            running = Assert.Single(_shell.Exports.Running);
            Assert.Empty(_copied);
        }, [Row(2m), Row(7m)], [Row(2m)]);

        await CopyAllAsync(ColumnValuesFormat.Lines, all);

        Assert.False(distinct);
        Assert.Equal("ART → Zwischenablage", running!.Label);
        Assert.Contains("ART", running.Title, StringComparison.Ordinal);
        Assert.Empty(_shell.Exports.Running);
        Assert.Equal(["Gewerbe (2)\r\n7\r\nGewerbe (2)"], _copied);
        Assert.Equal("3 Werte von ART kopiert.", _shell.Notice!.Text);
    }

    [Fact]
    public async Task An_in_list_of_all_values_asks_for_each_value_once()
    {
        bool? distinct = null;

        await CopyAllAsync(ColumnValuesFormat.InList, Blocks(d => distinct = d, [Row(2m), Row(7m)]));

        Assert.True(distinct);
        Assert.Equal(["2, 7"], _copied);
    }

    [Fact]
    public async Task A_distinct_read_names_null_without_a_number()
    {
        var all = Blocks(null, [Row(2m), Row((object?)null)]) with { DistinctInQuery = true };

        await CopyAllAsync(ColumnValuesFormat.InList, all);

        Assert.Equal(["NULL-Werte stehen nicht in der Liste: IN findet NULL nie."], _shell.Notice!.Warnings);
    }

    [Fact]
    public async Task A_result_counts_its_null_rows()
    {
        await CopyAllAsync(ColumnValuesFormat.InList, Blocks(null, [Row(2m), Row((object?)null), Row((object?)null)]));

        Assert.Equal(["2 NULL-Werte stehen nicht in der Liste: IN findet NULL nie."], _shell.Notice!.Warnings);
    }

    [Fact]
    public async Task More_values_than_the_limit_stop_the_read_and_copy_nothing()
    {
        var blocks = Enumerable.Range(0, ColumnValues.MaxValues / IDataAccess.BlockSize + 2)
            .Select(b => (IReadOnlyList<RowData>)[.. Enumerable.Range(0, IDataAccess.BlockSize).Select(i => Row((decimal)(b * IDataAccess.BlockSize + i)))])
            .ToArray();
        var read = 0;
        var all = new AllColumnValues("", null, _ => async (onBlock, ct) =>
        {
            foreach (var block in blocks)
            {
                read++;
                if (!await onBlock(block, Substitute.For<IRowLookup>(), ct))
                {
                    return;
                }
            }
        }, 0, "");

        await CopyAllAsync(ColumnValuesFormat.Lines, all);

        Assert.Equal(ColumnValues.MaxValues / IDataAccess.BlockSize + 1, read); // the block with the value too many is the last one read
        Assert.Empty(_copied);
        // The number follows the machine's culture, not the UI language.
        Assert.Equal($"Mehr als {ColumnValues.MaxValues:N0} Werte – nichts kopiert. So viele Werte besser über „Alle Zeilen“ als CSV oder Excel speichern.", _shell.Notice!.Text);
    }

    [Fact]
    public async Task Cancelling_copies_nothing_and_says_so()
    {
        var all = new AllColumnValues("", null, _ => async (onBlock, ct) =>
        {
            await onBlock([Row(2m)], Substitute.For<IRowLookup>(), ct);
            _shell.Exports.Running[0].Cancel(); // the status bar's button
            ct.ThrowIfCancellationRequested();
        }, 0, "");

        await CopyAllAsync(ColumnValuesFormat.Lines, all);

        Assert.Empty(_copied);
        Assert.Equal("Kopieren der Werte von ART abgebrochen.", _shell.Notice!.Text);
        Assert.Empty(_shell.Exports.Running);
    }

    [Fact]
    public async Task A_database_error_opens_the_error_dialog()
    {
        var all = new AllColumnValues("", null, _ => (_, _) => throw new DatabaseException("Snapshot zu alt", "ORA-01555"), 0, "");

        await CopyAllAsync(ColumnValuesFormat.Lines, all);

        Assert.Equal("ORA-01555", _shell.ErrorDetails!.ErrorCode);
        Assert.Empty(_copied);
        Assert.Empty(_shell.Exports.Running);
    }
}
