using FerretSharp.Core.Connections;
using FerretSharp.Core.Data;
using FerretSharp.Core.Query;
using FerretSharp.Core.Relations;
using FerretSharp.Core.Schema;
using FerretSharp.UI.State;
using NSubstitute;

namespace FerretSharp.UI.Tests.State;

/// <summary>Copy to another connection (WP-42): where it can go, what the dialog chooses, and the write as one action.</summary>
public sealed class SubgraphCopyTests : IAsyncDisposable
{
    private readonly TestApp _app = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ColumnInfo Col(string name) => new(name, "NUMBER", null, false, 10, 0, true, false, null, 0);

    private static TableDetails Table(string owner, string name, params string[] columns) =>
        new(new TableSummary(owner, name, TableKind.Table), [.. columns.Select(Col)], [columns[0]], [], false);

    private static readonly TableDetails Kunde = Table("APP", "KUNDE", "ID");
    private static readonly TableDetails Auftrag = Table("APP", "AUFTRAG", "ID", "KUNDE_ID");
    private static readonly TableDetails Position = Table("APP", "POSITION", "ID", "AUFTRAG_ID");
    private static readonly ForeignKeyInfo AuftragKunde = new("FK_AUFTRAG_KUNDE", Auftrag.Table.Ref, ["KUNDE_ID"], Kunde.Table.Ref, ["ID"], FkSource.Declared);
    private static readonly ForeignKeyInfo PositionAuftrag = new("FK_POSITION_AUFTRAG", Position.Table.Ref, ["AUFTRAG_ID"], Auftrag.Table.Ref, ["ID"], FkSource.Declared);
    private static readonly RelationGraph Relations = new([AuftragKunde, PositionAuftrag]);

    /// <summary>The source has customer 1 with order 10 and its position 100; the target has the same tables, empty.</summary>
    /// <param name="targetError">What reading in the target throws; null: nothing.</param>
    private static async Task<SubgraphCopyDraft> DraftAsync(bool writable = true, Exception? targetError = null)
    {
        var rows = new Dictionary<TableRef, RowData[]>
        {
            [Kunde.Table.Ref] = [new(new RowKey.PrimaryKey([1m]), [1m])],
            [Auftrag.Table.Ref] = [new(new RowKey.PrimaryKey([10m]), [10m, 1m])],
            [Position.Table.Ref] = [new(new RowKey.PrimaryKey([100m]), [100m, 10m])],
        };
        var source = Substitute.For<IDataAccess>();
        source.ReadByKeysAsync(default!, default, default!, default, default, default).ReturnsForAnyArgs(call =>
        {
            var table = call.ArgAt<TableDetails>(0);
            var column = call.ArgAt<IReadOnlyList<int>?>(1)![0];
            var keys = call.ArgAt<IReadOnlyList<IReadOnlyList<object>>>(2).Select(k => k[0]).ToHashSet();
            return Task.FromResult<IReadOnlyList<RowData>>([.. rows[table.Table.Ref].Where(r => keys.Contains(r.Values[column]!))]);
        });
        var target = Substitute.For<IDataAccess>();
        target.ReadByKeysAsync(default!, default, default!, default, default, default).ReturnsForAnyArgs(
            targetError is null ? Task.FromResult<IReadOnlyList<RowData>>([]) : Task.FromException<IReadOnlyList<RowData>>(targetError));

        var reader = Substitute.For<ISchemaReader>();
        TableDetails[] targetTables = [.. new[] { Kunde, Auftrag, Position }.Select(t => t with { Table = t.Table with { Owner = "TEST" } })];
        reader.GetTablesAsync("TEST", Arg.Any<CancellationToken>()).Returns([.. targetTables.Select(t => t.Table)]);
        reader.GetSynonymTargetsAsync("TEST", Arg.Any<CancellationToken>()).Returns(SynonymTargets.None);
        reader.GetPlSqlObjectsAsync("TEST", Arg.Any<CancellationToken>()).Returns([]);
        reader.GetForeignKeysAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).Returns([]);
        reader.GetPrimaryKeysAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).Returns([]);
        reader.GetDetailsAsync(Arg.Any<TableSummary>(), Arg.Any<CancellationToken>()).Returns(call => targetTables.Single(t => t.Table == call.Arg<TableSummary>()));
        reader.GetTableTriggersAsync(Arg.Any<TableRef>(), Arg.Any<CancellationToken>()).Returns([]);
        var schema = new SchemaCache(reader, "TEST");
        await schema.LoadAsync(Ct);

        var all = new[] { Kunde, Auftrag, Position }.ToDictionary(t => t.Table.Ref);
        return new SubgraphCopyDraft(
            "DEV", "APP", Relations, Kunde, [new RowKey.PrimaryKey([1m])], _ => Task.FromResult(source),
            (table, _) => Task.FromResult(all.GetValueOrDefault(table)),
            [new CopyTarget(Guid.NewGuid(), Guid.NewGuid(), "TEST · Workspace 1", ConnectionKind.Test, () => writable, schema, _ => Task.FromResult(target))]);
    }

    [Fact]
    public async Task By_default_the_children_one_level_deep_are_chosen_and_deeper_paths_bring_their_prefix()
    {
        var draft = await DraftAsync();

        Assert.Equal([AuftragKunde], draft.ChildrenBelow([]));
        Assert.True(draft.IsChosen([AuftragKunde]));
        Assert.Equal([PositionAuftrag], draft.ChildrenBelow([AuftragKunde]));
        Assert.False(draft.IsChosen([AuftragKunde, PositionAuftrag]));

        draft.Toggle([AuftragKunde]);
        draft.Toggle([AuftragKunde, PositionAuftrag]);
        Assert.True(draft.IsChosen([AuftragKunde]));
        Assert.True(draft.IsChosen([AuftragKunde, PositionAuftrag]));

        draft.Toggle([AuftragKunde]);
        Assert.False(draft.IsChosen([AuftragKunde, PositionAuftrag])); // off takes the longer path along
        Assert.Empty(draft.Traversal.Children);
    }

    [Fact]
    public async Task Paths_end_at_three_levels()
    {
        var draft = await DraftAsync();

        Assert.Empty(draft.ChildrenBelow([AuftragKunde, PositionAuftrag, PositionAuftrag]));
    }

    [Fact]
    public async Task Reading_plans_the_copy_and_a_changed_choice_asks_to_read_again()
    {
        var draft = await DraftAsync();
        draft.Toggle([AuftragKunde, PositionAuftrag]);

        await draft.LoadAsync(null, Ct);

        Assert.Equal(CopyStage.Planned, draft.Stage);
        Assert.Equal(3, draft.Graph!.RowCount);
        Assert.Equal(["KUNDE", "AUFTRAG", "POSITION"], draft.Plan!.Tables.Select(t => t.Table.Name));
        Assert.True(draft.CanCopy);
        var write = draft.Write(5);
        Assert.Equal("Kopie aus DEV: 3 Zeilen", write.Description);
        Assert.All(write.Steps, s => Assert.Equal("TEST", s.Table.Table.Owner));

        draft.ToggleParent(AuftragKunde);
        Assert.Equal(CopyStage.Choosing, draft.Stage);
        Assert.False(draft.CanCopy);
        Assert.Throws<InvalidOperationException>(() => draft.Write(5));
        Assert.Equal([RelationGraph.KeyOf(AuftragKunde)], draft.Traversal.SkippedParents);
    }

    [Fact]
    public async Task The_parent_tree_starts_at_the_start_table_and_the_tables_of_the_chosen_paths_and_counts_after_reading()
    {
        var draft = await DraftAsync();
        Assert.Equal([Kunde.Table.Ref, Auftrag.Table.Ref], draft.ParentRoots);
        draft.Toggle([AuftragKunde, PositionAuftrag]);
        Assert.Equal([Kunde.Table.Ref, Auftrag.Table.Ref, Position.Table.Ref], draft.ParentRoots);
        Assert.Null(draft.ReachedAlong([AuftragKunde]));

        await draft.LoadAsync(null, Ct);

        Assert.Equal(1, draft.ReachedAlong([AuftragKunde]));
        Assert.Equal(1, draft.ReachedAlong([AuftragKunde, PositionAuftrag]));
        Assert.Equal(1, draft.ParentsAlong(AuftragKunde));
        draft.ToggleParent(PositionAuftrag);
        Assert.Null(draft.ParentsAlong(PositionAuftrag)); // switched off, and the read no longer fits
    }

    /// <summary>E2E: under the orders of a customer the tree showed ORDER → CUSTOMER again, the way back to the start rows.</summary>
    [Fact]
    public async Task The_parent_tree_leaves_out_the_relationships_of_the_chosen_paths()
    {
        var draft = await DraftAsync();
        Assert.Empty(draft.ParentsBelow(Auftrag.Table.Ref));
        Assert.Equal([PositionAuftrag], draft.ParentsBelow(Position.Table.Ref)); // not on a chosen path yet

        draft.Toggle([AuftragKunde, PositionAuftrag]);
        Assert.Empty(draft.ParentsBelow(Position.Table.Ref));

        draft.Toggle([AuftragKunde]);
        Assert.Equal([AuftragKunde], draft.ParentsBelow(Auftrag.Table.Ref));
    }

    [Fact]
    public async Task A_target_that_may_not_write_plans_but_does_not_copy()
    {
        var draft = await DraftAsync(writable: false);

        await draft.LoadAsync(null, Ct);

        Assert.Equal(CopyStage.Planned, draft.Stage);
        Assert.False(draft.CanCopy);
    }

    [Fact]
    public async Task Reading_the_source_and_checking_the_target_are_separate_steps_each_with_its_own_errors()
    {
        var draft = await DraftAsync(targetError: new DatabaseException("ORA-03113: end-of-file on communication channel"));

        Assert.True(await draft.CollectAsync(null, Ct));
        Assert.Equal(CopyStage.Loading, draft.Stage);

        await Assert.ThrowsAsync<DatabaseException>(() => draft.PlanAsync(Ct));
        Assert.Equal(CopyStage.Choosing, draft.Stage);
        Assert.Null(draft.Plan);

        await draft.PlanAsync(Ct); // nothing collected any more: does nothing
        Assert.Equal(CopyStage.Choosing, draft.Stage);
    }

    [Fact]
    public async Task Targets_are_the_workspaces_of_the_other_connections_a_locked_prod_among_them()
    {
        var test = await _app.OpenAsync(TestApp.Profile("Test"));
        await _app.OpenAsync(TestApp.Profile("Prod", ConnectionKind.Prod));
        await _app.OpenAsync(TestApp.Profile("Dev", ConnectionKind.Dev));

        var targets = CopyTargets.Targets(_app.Hub, test.Active, _app.Settings.Current);

        Assert.Equal(["Prod · Workspace 1", "Dev · Workspace 1"], targets.Select(t => t.Label));
        Assert.Equal([false, true], targets.Select(t => t.Writable));
        Assert.DoesNotContain(targets, t => t.ConnectionId == test.Id);
        Assert.All(targets, t => Assert.Same(_app.Hub.Open.Single(s => s.Id == t.ConnectionId).Active, t.Connection));
    }

    [Fact]
    public async Task A_target_unlocked_while_the_dialog_is_open_may_write()
    {
        var test = await _app.OpenAsync(TestApp.Profile("Test"));
        var prod = await _app.OpenAsync(TestApp.Profile("Prod", ConnectionKind.Prod));
        var target = Assert.Single(CopyTargets.Targets(_app.Hub, test.Active, _app.Settings.Current));
        Assert.False(target.Writable);

        await prod.Workspaces.UnlockAsync(target.WorkspaceId, Ct);

        Assert.True(target.Writable);
    }

    [Fact]
    public async Task A_copy_is_one_action_of_the_target_and_a_locked_target_refuses_it()
    {
        var test = await _app.OpenAsync(TestApp.Profile("Test"));
        var prod = await _app.OpenAsync(TestApp.Profile("Prod", ConnectionKind.Prod));
        var details = _app.KundenDetails;
        var copy = new CopyWrite("Kopie aus DEV: 1 Zeile", [new CopyInsert(details, DmlBuilder.InsertBatch(details, [0], [[1m]]))]);

        var action = await _app.Editing.CopyAsync(_app.WorkspaceOf(test), copy);

        Assert.NotNull(action);
        Assert.Equal(WriteActionKind.Copy, action.Kind);
        Assert.Equal([action], test.Workspaces.ActionsOf(test.Workspaces.Active!.Id));
        Assert.Null(await _app.Editing.CopyAsync(_app.WorkspaceOf(prod), copy));
        Assert.Equal(Core.Resources.OracleText.WorkspaceReadOnly, _app.Shell.Notice?.Text);
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();
}
