using FerretSharp.Core.Connections;
using FerretSharp.Core.Data;
using FerretSharp.Core.Schema;
using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>
/// Changed primary keys (ADR 0007 addendum, the user's decision): the rows still pointing to the old keys are counted before
/// writing, and the write waits for the user if there are any.
/// </summary>
public sealed class KeyChangeQuestionTests : IAsyncDisposable
{
    private static readonly TableDetails Kunden = new(
        TestApp.Kunden,
        [
            new ColumnInfo("ID", "NUMBER", null, false, 10, 0, false, false, null, 1),
            new ColumnInfo("NAME", "VARCHAR2", 20, true, null, null, true, false, null, 2),
        ],
        ["ID"], [], false);

    private static readonly ForeignKeyInfo AuftragKunde = new(
        "FK_AUFTRAG_KUNDE", new TableRef("APP_USER", "AUFTRAG"), ["KUNDE_ID"], new TableRef("APP_USER", "KUNDEN"), ["ID"], FkSource.Declared);

    private static readonly TableDetails Auftrag = new(
        new TableSummary("APP_USER", "AUFTRAG", TableKind.Table),
        [
            new ColumnInfo("ID", "NUMBER", null, false, 10, 0, false, false, null, 1),
            new ColumnInfo("KUNDE_ID", "NUMBER", null, false, 10, 0, true, false, null, 2),
        ],
        ["ID"], [], false);

    private readonly TestApp _app = NewApp();

    private static TestApp NewApp()
    {
        var app = new TestApp { ForeignKeys = [AuftragKunde] };
        app.OtherDetails["AUFTRAG"] = Auftrag;
        return app;
    }

    private static RowData Row(decimal id, string name) => new(new RowKey.PrimaryKey([id]), [id, name]);

    private async Task<(WorkspaceTabs Workspace, TableTab Tab, FakeDataEditor Editor)> OpenAsync()
    {
        var scope = await _app.OpenAsync(TestApp.Profile("Test"));
        var workspace = _app.WorkspaceOf(scope);
        var tab = new TableTab(workspace.WorkspaceId, TestApp.Kunden) { Changes = new ChangeTracker(Kunden) };
        workspace.Tabs.Add(tab);
        var editor = (FakeDataEditor)await scope.Workspaces.GetEditorAsync(workspace.WorkspaceId, CancellationToken.None);
        return (workspace, tab, editor);
    }

    [Fact]
    public async Task A_changed_key_rows_point_to_is_committed_only_after_the_user_agrees()
    {
        var (workspace, tab, editor) = await OpenAsync();
        _app.CountRows = filters => filters is [{ Column: "KUNDE_ID", Values: ["1"] }] ? 3 : 0;
        var commits = 0;
        _app.OnCommit = () =>
        {
            commits++;
            return Task.CompletedTask;
        };
        tab.Changes!.SetValue(Row(1, "Meier"), 0, "7");

        Assert.False(await _app.Editing.CommitAsync(workspace));

        var question = _app.Editing.KeyQuestion;
        Assert.NotNull(question);
        Assert.True(question.DuringCommit);
        var reference = Assert.Single(question.References);
        Assert.Equal(("AUFTRAG", new FkCount.Done(3)), (reference.Table, reference.Count));
        Assert.Empty(editor.Actions); // nothing written yet
        Assert.Equal(1, tab.Changes.PendingCount);

        await _app.Editing.AnswerKeyQuestionAsync(write: true);

        Assert.Null(_app.Editing.KeyQuestion);
        Assert.Equal(1, commits);
        Assert.Equal((0, 0), (tab.Changes.PendingCount, tab.Changes.FlushedCount));
    }

    [Fact]
    public async Task Cancel_leaves_the_change_pending_and_the_next_write_asks_again()
    {
        var (workspace, tab, editor) = await OpenAsync();
        _app.CountRows = _ => 1;
        tab.Changes!.SetValue(Row(1, "Meier"), 0, "7");
        Assert.False(await _app.Editing.FlushAsync(workspace));

        await _app.Editing.AnswerKeyQuestionAsync(write: false);

        Assert.Null(_app.Editing.KeyQuestion);
        Assert.Empty(editor.Actions);
        Assert.Equal(1, tab.Changes.PendingCount);
        Assert.False(await _app.Editing.FlushAsync(workspace));
        Assert.NotNull(_app.Editing.KeyQuestion);
    }

    [Fact]
    public async Task A_key_no_row_points_to_and_other_columns_are_written_without_asking()
    {
        var (workspace, tab, _) = await OpenAsync();
        var counted = 0;
        _app.CountRows = _ =>
        {
            counted++;
            return 0;
        };
        tab.Changes!.SetValue(Row(1, "Meier"), 0, "7");
        tab.Changes.SetValue(Row(2, "Lang"), 1, "Kurz");

        Assert.True(await _app.Editing.FlushAsync(workspace));

        Assert.Null(_app.Editing.KeyQuestion);
        Assert.Equal(1, counted); // only the changed key
        Assert.Equal(2, tab.Changes.FlushedCount);
    }

    /// <summary>A count Oracle refuses (no rights on the referencing table) must not stop the write: the user decides.</summary>
    [Fact]
    public async Task A_count_that_fails_is_shown_and_the_user_decides()
    {
        var (workspace, tab, _) = await OpenAsync();
        _app.CountRows = _ => throw new DatabaseException("ORA-00942: Tabelle oder View nicht vorhanden", "ORA-00942");
        tab.Changes!.SetValue(Row(1, "Meier"), 0, "7");

        Assert.False(await _app.Editing.FlushAsync(workspace));

        var failed = Assert.IsType<FkCount.Failed>(Assert.Single(_app.Editing.KeyQuestion!.References).Count);
        Assert.StartsWith("ORA-00942", failed.Message);
        await _app.Editing.AnswerKeyQuestionAsync(write: true);
        Assert.Equal(1, tab.Changes.FlushedCount);
    }

    /// <summary>
    /// Review of `small-pk-edit`, 1: a relationship that no longer fits its table (here: the column is gone) made the filter
    /// check refuse – not an Oracle error, so it stopped every write instead of being shown as not counted.
    /// </summary>
    [Fact]
    public async Task A_relationship_the_table_no_longer_has_the_column_of_is_shown_as_not_counted()
    {
        var (workspace, tab, _) = await OpenAsync();
        _app.OtherDetails["AUFTRAG"] = Auftrag with { Columns = [Auftrag.Columns[0]] };
        tab.Changes!.SetValue(Row(1, "Meier"), 0, "7");

        Assert.False(await _app.Editing.FlushAsync(workspace));

        Assert.Null(_app.Shell.ErrorDetails);
        var failed = Assert.IsType<FkCount.Failed>(Assert.Single(_app.Editing.KeyQuestion!.References).Count);
        Assert.Contains("KUNDE_ID", failed.Message);
        await _app.Editing.AnswerKeyQuestionAsync(write: true);
        Assert.Equal(1, tab.Changes.FlushedCount);
    }

    /// <summary>
    /// Review of `small-pk-edit`, 4: every relationship was counted for up to 5 s, one after the other, and after 5 s the write
    /// counted as stuck ("give up the session" rolls the transaction back). All counts share 10 s now; the rest is not counted.
    /// </summary>
    [Fact]
    public async Task Counting_shares_one_budget_and_never_counts_as_stuck()
    {
        foreach (var name in new[] { "RECHNUNG", "VERTRAG" })
        {
            _app.OtherDetails[name] = Auftrag with { Table = new TableSummary("APP_USER", name, TableKind.Table) };
        }

        _app.ForeignKeys =
        [
            AuftragKunde,
            AuftragKunde with { Name = "FK_RECHNUNG_KUNDE", From = new TableRef("APP_USER", "RECHNUNG") },
            AuftragKunde with { Name = "FK_VERTRAG_KUNDE", From = new TableRef("APP_USER", "VERTRAG") },
        ];
        var (workspace, tab, _) = await OpenAsync();
        var stuck = new List<bool>();
        _app.CountRows = _ =>
        {
            _app.Time.Advance(TimeSpan.FromSeconds(6)); // an unindexed column over the VPN
            stuck.Add(_app.Editing.IsStuck(workspace.WorkspaceId));
            return 1;
        };
        tab.Changes!.SetValue(Row(1, "Meier"), 0, "7");

        Assert.False(await _app.Editing.FlushAsync(workspace));

        Assert.Equal([false, false], stuck); // the third was not counted any more
        var counts = _app.Editing.KeyQuestion!.References.Select(r => r.Count).ToList();
        Assert.Equal([new FkCount.Done(1), new FkCount.Done(1)], counts.Take(2));
        Assert.Contains("10 s", Assert.IsType<FkCount.Failed>(counts[2]).Message);
    }

    /// <summary>
    /// Review of `small-pk-edit`, 7: "write anyway" stayed confirmed for the row when the commit was refused before writing (the
    /// transaction had ended) – a later, other key of the same row was written without counting.
    /// </summary>
    [Fact]
    public async Task A_confirmation_covers_only_the_values_confirmed()
    {
        var (workspace, tab, _) = await OpenAsync();
        _app.CountRows = _ => 1;
        tab.Changes!.SetValue(Row(2, "Lang"), 1, "Kurz");
        Assert.True(await _app.Editing.FlushAsync(workspace)); // something written: the commit checks the transaction
        tab.Changes.SetValue(Row(1, "Meier"), 0, "7");
        Assert.False(await _app.Editing.CommitAsync(workspace));
        _app.EndTransactions();

        await _app.Editing.AnswerKeyQuestionAsync(write: true); // refused before writing: the transaction is gone
        Assert.StartsWith("Nicht committet: Die Transaktion endete ohne Commit", _app.Shell.Notice?.Text);

        tab.Changes.SetValue(Row(1, "Meier"), 0, "8");
        Assert.False(await _app.Editing.FlushAsync(workspace));
        Assert.NotNull(_app.Editing.KeyQuestion);
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();
}
