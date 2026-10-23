using FerretSharp.Core.Data;
using FerretSharp.Core.Relations;
using FerretSharp.Core.Schema;
using FerretSharp.UI.State;
using NSubstitute;

namespace FerretSharp.UI.Tests.State;

/// <summary>WP-60: the record diagram of a tab – a new one expands its start rows, F5 does the steps again, reset starts over.</summary>
public sealed class RecordDiagramStateTests
{
    private static readonly TableDetails Kunde = Table("KUNDE", Col("ID"));
    private static readonly TableDetails Auftrag = Table("AUFTRAG", Col("ID"), Col("KUNDE_ID"));
    private static readonly TableDetails Position = Table("POSITION", Col("ID"), Col("AUFTRAG_ID"));

    private static readonly ForeignKeyInfo AuftragKunde = new("FK_AUFTRAG_KUNDE", Auftrag.Table.Ref, ["KUNDE_ID"], Kunde.Table.Ref, ["ID"], FkSource.Declared);
    private static readonly ForeignKeyInfo PositionAuftrag = new("FK_POSITION_AUFTRAG", Position.Table.Ref, ["AUFTRAG_ID"], Auftrag.Table.Ref, ["ID"], FkSource.Declared);
    private static readonly RelationGraph Relations = new([AuftragKunde, PositionAuftrag]);

    private readonly Dictionary<TableRef, List<object?[]>> _rows = new()
    {
        [Kunde.Table.Ref] = [[1m], [2m]],
        [Auftrag.Table.Ref] = [[100m, 1m], [101m, 1m], [102m, 2m]],
        [Position.Table.Ref] = [[1000m, 100m], [1001m, 100m]],
    };

    private readonly IDataAccess _data = Substitute.For<IDataAccess>();

    /// <summary>Thrown by every read once set (a lost session).</summary>
    private Exception? _failure;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public RecordDiagramStateTests()
    {
        _data.ReadByKeysAsync(default!, default, default!, default, default, default).ReturnsForAnyArgs(call =>
        {
            if (_failure is not null)
            {
                return Task.FromException<IReadOnlyList<RowData>>(_failure);
            }

            var table = call.ArgAt<TableDetails>(0);
            var columns = call.ArgAt<IReadOnlyList<int>?>(1)!;
            var keys = call.ArgAt<IReadOnlyList<IReadOnlyList<object>>>(2);
            return Task.FromResult<IReadOnlyList<RowData>>([.. _rows[table.Table.Ref]
                .Where(row => keys.Any(key => columns.Select((c, i) => Equals(row[c], key[i])).All(same => same)))
                .Select(row => new RowData(new RowKey.PrimaryKey([row[0]]), row))]);
        });
    }

    private static ColumnInfo Col(string name) => new(name, "NUMBER", null, false, 10, 0, name != "ID", false, null, 0);

    private static TableDetails Table(string name, params ColumnInfo[] columns) =>
        new(new TableSummary("APP", name, TableKind.Table), columns, ["ID"], [], false);

    private static RecordNode Node(TableDetails table, decimal id) => new(table.Table.Ref, new RowKey.PrimaryKey([id]));

    private RowTraversal Traversal() =>
        new(_data, Relations, (t, _) => Task.FromResult<TableDetails?>(new[] { Kunde, Auftrag, Position }.FirstOrDefault(d => d.Table.Ref == t)), wholeLobs: false);

    private static RecordDiagramState NewState(params string[] ids) => new(Auftrag.Table.Ref, rowIds: false, [.. ids.Select(id => (IReadOnlyList<string>)[id])], steps: null);

    private Task LoadAsync(RecordDiagramState state) => state.LoadAsync(Traversal(), Relations, Auftrag, null, Ct);

    [Fact]
    public async Task A_new_diagram_expands_its_start_rows_on_both_sides_and_counts_as_unchanged()
    {
        var state = NewState("100");

        await LoadAsync(state);

        Assert.Equal([Node(Auftrag, 100m), Node(Kunde, 1m), Node(Position, 1000m), Node(Position, 1001m)], state.Diagram!.Rows.Select(r => r.Node));
        Assert.Equal(2, state.Steps.Count);
        Assert.False(state.Changed);
        Assert.NotNull(state.Placement);
    }

    [Fact]
    public async Task Reloading_does_the_steps_again_and_keeps_the_order_of_the_rows()
    {
        var state = NewState("100");
        await LoadAsync(state);
        await state.ExpandAsync(Node(Kunde, 1m), DiagramSide.Children, null, Ct);
        Assert.True(state.Changed);
        var before = state.Placement!.Nodes.Values.OrderBy(b => b.Id, StringComparer.Ordinal).Select(b => (b.Id, b.Layer, b.Order)).ToList();

        await LoadAsync(state);

        Assert.Equal(before, state.Placement!.Nodes.Values.OrderBy(b => b.Id, StringComparer.Ordinal).Select(b => (b.Id, b.Layer, b.Order)));
        Assert.Contains(state.Diagram!.Rows, r => r.Node == Node(Auftrag, 101m));
    }

    [Fact]
    public async Task Saved_steps_are_done_when_a_restored_tab_is_loaded()
    {
        var first = NewState("100");
        await LoadAsync(first);
        await first.ExpandAsync(Node(Kunde, 1m), DiagramSide.Children, null, Ct);

        var restored = new RecordDiagramState(Auftrag.Table.Ref, rowIds: false, first.Keys, first.Steps);
        await LoadAsync(restored);

        Assert.Equal(first.Diagram!.Rows, restored.Diagram!.Rows);
        Assert.True(restored.Changed);
    }

    [Fact]
    public async Task Reset_goes_back_to_the_start_rows_expanded_on_both_sides()
    {
        var state = NewState("100");
        await LoadAsync(state);
        await state.ExpandAsync(Node(Kunde, 1m), DiagramSide.Children, null, Ct);

        state.Reset();
        Assert.Null(state.Diagram);
        await LoadAsync(state);

        Assert.DoesNotContain(state.Diagram!.Rows, r => r.Node == Node(Auftrag, 101m));
        Assert.False(state.Changed);
    }

    [Fact]
    public async Task A_failed_load_keeps_the_diagram_shown()
    {
        var state = NewState("100");
        await LoadAsync(state);
        var shown = state.Diagram;
        _failure = new InvalidOperationException("ORA-03113");

        await Assert.ThrowsAsync<InvalidOperationException>(() => LoadAsync(state));

        Assert.Same(shown, state.Diagram);
    }

    [Fact]
    public async Task Start_rows_that_are_gone_leave_an_empty_diagram()
    {
        var state = NewState("999", "x");

        await LoadAsync(state);

        Assert.True(state.StartGone);
        Assert.Empty(state.Diagram!.Rows);
    }

    [Fact]
    public async Task Marked_rows_of_one_table_can_start_a_copy()
    {
        var state = NewState("100");
        await LoadAsync(state);

        state.ToggleMark(Node(Position, 1000m));
        state.ToggleMark(Node(Position, 1001m));
        Assert.Equal(Position.Table.Ref, state.MarkedTable);

        state.ToggleMark(Node(Kunde, 1m));
        Assert.Null(state.MarkedTable); // rows of two tables
        state.ToggleMark(Node(Kunde, 1m));
        state.ToggleMark(Node(Kunde, 2m)); // not shown: not marked
        Assert.Equal(2, state.Marked.Count);

        state.Reset();
        Assert.Empty(state.Marked);
    }
}
