using FerretSharp.Core;
using FerretSharp.Core.Data;
using FerretSharp.Core.Relations;
using FerretSharp.Core.Schema;
using FerretSharp.Core.Workspaces;
using FerretSharp.UI.State;
using NSubstitute;

namespace FerretSharp.UI.Tests.State;

/// <summary>WP-60: the record diagram of a tab – a new one expands its start rows, F5 does the steps again, reset starts over.</summary>
public sealed class RecordDiagramStateTests
{
    private static readonly TableDetails Kunde = Table("KUNDE", Col("ID"));
    private static readonly TableDetails Auftrag = Table("AUFTRAG", Col("ID"), Col("KUNDE_ID"));
    private static readonly TableDetails Position = Table("POSITION", Col("ID"), Col("AUFTRAG_ID"));
    private static readonly TableDetails Notiz = Table("NOTIZ", Col("ID"), Col("AUFTRAG_ID"));

    private static readonly ForeignKeyInfo AuftragKunde = new("FK_AUFTRAG_KUNDE", Auftrag.Table.Ref, ["KUNDE_ID"], Kunde.Table.Ref, ["ID"], FkSource.Declared);
    private static readonly ForeignKeyInfo PositionAuftrag = new("FK_POSITION_AUFTRAG", Position.Table.Ref, ["AUFTRAG_ID"], Auftrag.Table.Ref, ["ID"], FkSource.Declared);
    private static readonly ForeignKeyInfo NotizAuftrag = new("FK_NOTIZ_AUFTRAG", Notiz.Table.Ref, ["AUFTRAG_ID"], Auftrag.Table.Ref, ["ID"], FkSource.Declared);
    private static readonly RelationGraph Relations = new([AuftragKunde, PositionAuftrag]);

    private readonly Dictionary<TableRef, List<object?[]>> _rows = new()
    {
        [Kunde.Table.Ref] = [[1m], [2m]],
        [Auftrag.Table.Ref] = [[100m, 1m], [101m, 1m], [102m, 2m]],
        [Position.Table.Ref] = [[1000m, 100m], [1001m, 100m]],
        [Notiz.Table.Ref] = [[5000m, 100m]],
    };

    private readonly IDataAccess _data = Substitute.For<IDataAccess>();

    /// <summary>Thrown by every read once set (a lost session).</summary>
    private Exception? _failure;

    /// <summary>Reads so far, and the one that waits for <see cref="_hold"/> (to do something while a load is under way).</summary>
    private int _reads;
    private int _holdAt;
    private TaskCompletionSource? _hold;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public RecordDiagramStateTests()
    {
        _data.ReadByKeysAsync(default!, default, default!, default, default, default).ReturnsForAnyArgs(call => ReadAsync(
            call.ArgAt<TableDetails>(0), call.ArgAt<IReadOnlyList<int>?>(1)!, call.ArgAt<IReadOnlyList<IReadOnlyList<object>>>(2)));
    }

    private async Task<IReadOnlyList<RowData>> ReadAsync(TableDetails table, IReadOnlyList<int> columns, IReadOnlyList<IReadOnlyList<object>> keys)
    {
        if (_failure is not null)
        {
            throw _failure;
        }

        if (++_reads == _holdAt && _hold is { } hold)
        {
            await hold.Task;
        }

        return [.. _rows[table.Table.Ref]
            .Where(row => keys.Any(key => columns.Select((c, i) => Equals(row[c], key[i])).All(same => same)))
            .Select(row => new RowData(new RowKey.PrimaryKey([row[0]]), row))];
    }

    private static ColumnInfo Col(string name) => new(name, "NUMBER", null, false, 10, 0, name != "ID", false, null, 0);

    private static TableDetails Table(string name, params ColumnInfo[] columns) =>
        new(new TableSummary("APP", name, TableKind.Table), columns, ["ID"], [], false);

    private static RecordNode Node(TableDetails table, decimal id) => new(table.Table.Ref, new RowKey.PrimaryKey([id]));

    private RowTraversal Traversal(RelationGraph? relations = null) =>
        new(_data, relations ?? Relations, (t, _) => Task.FromResult<TableDetails?>(new[] { Kunde, Auftrag, Position, Notiz }.FirstOrDefault(d => d.Table.Ref == t)), wholeLobs: false);

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

    /// <summary>WP-62: the levels apply to opening; the two steps of a new diagram count as unchanged with them too.</summary>
    [Fact]
    public async Task A_new_diagram_goes_as_many_levels_as_set()
    {
        var state = new RecordDiagramState(Kunde.Table.Ref, rowIds: false, [["1"]], steps: null, depth: 2);

        await state.LoadAsync(Traversal(), Relations, Kunde, null, Ct);

        Assert.Equal([Node(Kunde, 1m), Node(Auftrag, 100m), Node(Auftrag, 101m), Node(Position, 1000m), Node(Position, 1001m)],
            state.Diagram!.Rows.Select(r => r.Node));
        Assert.Equal([2, 2], state.Steps.Select(s => s.Depth));
        Assert.False(state.Changed);
    }

    [Fact]
    public async Task Other_levels_build_an_unchanged_diagram_again()
    {
        var state = new RecordDiagramState(Kunde.Table.Ref, rowIds: false, [["1"]], steps: null);
        await state.LoadAsync(Traversal(), Relations, Kunde, null, Ct);
        Assert.DoesNotContain(state.Diagram!.Rows, r => r.Node == Node(Position, 1000m));

        Assert.False(state.SetDepth(1)); // the same
        Assert.True(state.SetDepth(2));
        Assert.NotNull(state.Diagram); // until the new one is there
        await state.LoadAsync(Traversal(), Relations, Kunde, null, Ct);

        Assert.Contains(state.Diagram!.Rows, r => r.Node == Node(Position, 1000m));
        Assert.Equal([2, 2], state.Steps.Select(s => s.Depth));
        Assert.False(state.Changed);
    }

    /// <summary>Review: the levels went through Reset – a cancelled or failed load left an empty tab, and the marks were gone.</summary>
    [Fact]
    public async Task Other_levels_keep_the_diagram_and_its_marks_if_the_load_fails()
    {
        var state = new RecordDiagramState(Kunde.Table.Ref, rowIds: false, [["1"]], steps: null);
        await state.LoadAsync(Traversal(), Relations, Kunde, null, Ct);
        var shown = state.Diagram;
        state.ToggleMark(Node(Auftrag, 100m));
        Assert.True(state.SetDepth(2));

        _failure = new InvalidOperationException("ORA-03113");
        await Assert.ThrowsAsync<InvalidOperationException>(() => state.LoadAsync(Traversal(), Relations, Kunde, null, Ct));
        Assert.Same(shown, state.Diagram);
        Assert.Equal([Node(Auftrag, 100m)], state.Marked);

        _failure = null;
        await state.LoadAsync(Traversal(), Relations, Kunde, null, Ct); // F5: opened again at the new levels
        Assert.Contains(state.Diagram!.Rows, r => r.Node == Node(Position, 1000m));
        Assert.Equal([Node(Auftrag, 100m)], state.Marked);
    }

    /// <summary>Review: with nothing loaded the levels were only noted, and the load replayed the saved ones.</summary>
    [Fact]
    public async Task Other_levels_before_the_first_load_open_a_restored_diagram_at_them()
    {
        var first = new RecordDiagramState(Kunde.Table.Ref, rowIds: false, [["1"]], steps: null);
        await first.LoadAsync(Traversal(), Relations, Kunde, null, Ct);
        var restored = new RecordDiagramState(Kunde.Table.Ref, rowIds: false, first.Keys, first.Steps);

        Assert.False(restored.SetDepth(2)); // nothing shown to load now
        await restored.LoadAsync(Traversal(), Relations, Kunde, null, Ct);

        Assert.Contains(restored.Diagram!.Rows, r => r.Node == Node(Position, 1000m));
        Assert.False(restored.Changed);
    }

    [Fact]
    public async Task A_restored_diagram_that_did_more_than_open_keeps_its_levels()
    {
        var first = new RecordDiagramState(Kunde.Table.Ref, rowIds: false, [["1"]], steps: null);
        await first.LoadAsync(Traversal(), Relations, Kunde, null, Ct);
        await first.ExpandAsync(Node(Auftrag, 101m), DiagramSide.Children, null, Ct);
        var restored = new RecordDiagramState(Kunde.Table.Ref, rowIds: false, first.Keys, first.Steps, depth: 2);

        await restored.LoadAsync(Traversal(), Relations, Kunde, null, Ct);

        Assert.Equal(first.Diagram!.Rows, restored.Diagram!.Rows);
        Assert.Equal([1, 1, 1], restored.Steps.Select(s => s.Depth));
    }

    [Fact]
    public async Task Other_levels_leave_a_changed_diagram_and_apply_to_the_next_handle()
    {
        var state = NewState("100");
        await LoadAsync(state);
        await state.ExpandAsync(Node(Kunde, 1m), DiagramSide.Children, null, Ct); // orders 101 (and 100)
        var rows = state.Diagram!.Rows.Count;

        Assert.False(state.SetDepth(2));
        Assert.Equal(rows, state.Diagram!.Rows.Count);
        Assert.Equal(2, state.Depth);
        await state.ExpandAsync(Node(Auftrag, 101m), DiagramSide.Children, null, Ct);

        Assert.Equal(2, state.Steps[^1].Depth);
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

    /// <summary>Review: start rows deleted in another workspace (not yet committed) come back after its rollback.</summary>
    [Fact]
    public async Task Start_rows_that_are_gone_keep_the_steps_for_when_they_are_back()
    {
        var state = NewState("100");
        await LoadAsync(state);
        await state.ExpandAsync(Node(Kunde, 1m), DiagramSide.Children, null, Ct);
        var row = _rows[Auftrag.Table.Ref][0];
        _rows[Auftrag.Table.Ref].Remove(row);

        await LoadAsync(state);
        Assert.True(state.StartGone);
        Assert.Equal(3, state.Steps.Count);

        _rows[Auftrag.Table.Ref].Insert(0, row);
        await LoadAsync(state);
        Assert.Contains(state.Diagram!.Rows, r => r.Node == Node(Auftrag, 101m));
    }

    /// <summary>Review: a tab saved before its first load ended has no steps, and came back without its parents and children.</summary>
    [Fact]
    public async Task A_tab_saved_without_steps_comes_back_as_a_new_diagram()
    {
        var saved = new RecordTabState("APP", "AUFTRAG", RowIds: false, [["100"]], []);

        var tab = RecordTab.Restore(Guid.NewGuid(), Auftrag.Table, saved);
        await LoadAsync(tab.Diagram);

        Assert.Contains(tab.Diagram.Diagram!.Rows, r => r.Node == Node(Kunde, 1m));
        Assert.False(tab.Diagram.Changed);
    }

    /// <summary>Review: the reload went over the old diagram's steps while a group of it was opened.</summary>
    [Fact]
    public async Task A_step_on_the_old_diagram_during_a_reload_does_not_break_it()
    {
        var state = NewState("100");
        await LoadAsync(state);
        var old = state.Diagram!;
        _hold = new TaskCompletionSource();
        _holdAt = _reads + 2; // the start rows, then the first step of the replay waits, inside its loop over the steps

        var load = LoadAsync(state);
        await old.ExpandAsync([Node(Kunde, 1m)], DiagramSide.Children, null, Ct); // a step on the diagram still shown
        _hold.SetResult();
        await load;

        Assert.Equal(2, state.Steps.Count);
    }

    [Fact]
    public async Task Start_rows_that_are_gone_leave_an_empty_diagram()
    {
        var state = NewState("999", "x");

        await LoadAsync(state);

        Assert.True(state.StartGone);
        Assert.Empty(state.Diagram!.Rows);
    }

    /// <summary>E2E: the saved state shared the diagram's list of steps, so the workspace saw no change and kept the old file.</summary>
    [Fact]
    public async Task The_saved_state_of_a_tab_does_not_change_with_later_steps()
    {
        var state = NewState("100");
        var tab = new RecordTab(Guid.NewGuid(), Auftrag.Table, state);
        await LoadAsync(state);
        var saved = tab.ToState([tab]).Records!;

        await state.ExpandAsync(Node(Kunde, 1m), DiagramSide.Children, null, Ct);

        Assert.Equal(2, saved.Steps.Count);
        Assert.Equal(3, tab.ToState([tab]).Records!.Steps.Count);
    }

    /// <summary>
    /// Error dialog: an expansion along two relationships reports its progress before the second read, which renders the tab;
    /// the rows of the first were in the diagram but not yet laid out, and the tab crashed on the missing box.
    /// </summary>
    [Fact]
    public async Task Rows_of_a_running_expansion_are_drawn_once_it_is_laid_out()
    {
        var relations = new RelationGraph([AuftragKunde, PositionAuftrag, NotizAuftrag]);
        var state = new RecordDiagramState(Auftrag.Table.Ref, rowIds: false, [["100"]], steps: []);
        await state.LoadAsync(Traversal(relations), relations, Auftrag, null, Ct);
        Assert.Equal([Node(Auftrag, 100m)], state.Drawn.Select(d => d.Node));

        var during = new List<(int Rows, int Drawn)>();
        var progress = new SyncProgress<TraversalProgress>(_ => during.Add((state.Diagram!.Rows.Count, state.Drawn.Count)));
        await state.ExpandAsync(Node(Auftrag, 100m), DiagramSide.Children, progress, Ct);

        Assert.Equal(2, during.Count);
        Assert.True(during[1].Rows > during[1].Drawn); // the rows of the first relationship are in, not yet laid out
        Assert.Equal(state.Diagram!.Rows.Count, state.Drawn.Count);
        Assert.Contains(state.Drawn, d => d.Node == Node(Notiz, 5000m));
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
