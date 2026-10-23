using FerretSharp.Core.Data;
using FerretSharp.Core.Relations;
using FerretSharp.Core.Schema;
using FerretSharp.Core.Workspaces;
using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>Record diagram tabs (WP-60): opening from rows, the title, saving and restoring, dropping them with their table.</summary>
public sealed class RecordTabTests : IAsyncDisposable
{
    private readonly TestApp _app = new();

    private static RowData Row(object id) => new(new RowKey.PrimaryKey([id]), [id]);

    [Fact]
    public async Task Rows_open_a_tab_after_the_active_one_named_after_the_table_and_key()
    {
        await _app.OpenAsync(TestApp.Profile("Test"));
        var table = _app.Shell.OpenTable(TestApp.Kunden)!;
        var sql = _app.Shell.OpenSql()!;
        _app.Shell.ActivateTab(table);

        var one = _app.Shell.OpenRecords(TestApp.Kunden, _app.KundenDetails, [Row(4711m)])!;
        _app.Shell.ActivateTab(table);
        var several = _app.Shell.OpenRecords(TestApp.Kunden, _app.KundenDetails, [Row(1m), Row(2m), Row(2m)])!;

        Assert.Equal([table, several, one, sql], _app.Shell.Tabs);
        Assert.Same(several, _app.Shell.ActiveTab);
        Assert.Equal("KUNDEN 4711", one.Caption);
        Assert.Equal("KUNDEN ×2", several.Caption); // the same key once
        Assert.Equal([["4711"]], one.Diagram.Keys);
    }

    [Fact]
    public async Task Rows_whose_key_has_no_exact_text_open_nothing()
    {
        await _app.OpenAsync(TestApp.Profile("Test"));
        var details = new TableDetails(TestApp.Kunden, [new ColumnInfo("ID", "BINARY_DOUBLE", null, false, null, null, false, false, null, 1)], ["ID"], [], false);

        Assert.Null(_app.Shell.OpenRecords(TestApp.Kunden, details, [Row(1.5d)]));
    }

    [Fact]
    public async Task Tabs_are_saved_with_keys_and_steps_and_restored_while_the_table_exists()
    {
        var scope = await _app.OpenAsync(TestApp.Profile("Test"));
        var tab = _app.Shell.OpenRecords(TestApp.Kunden, _app.KundenDetails, [Row(4711m)])!;
        var state = tab.ToState(_app.Shell.Tabs);
        Assert.Equal(("APP_USER", "KUNDEN", false), (state.Records!.Owner, state.Records.Name, state.Records.RowIds));
        Assert.Equal([["4711"]], state.Records.Keys);

        var step = new RecordStep(RecordStepKind.Expand, ["r:APP_USER.KUNDEN:4711"], DiagramSide.Children);
        var saved = new Workspace(Guid.NewGuid(), scope.Id, "Gespeichert")
        {
            Tabs = [state with { Records = state.Records with { Steps = [step] } }, TabState.OfRecords(new RecordTabState("APP_USER", "WEG", false, [["1"]], []))],
            ActiveTabIndex = 0,
        };
        _app.Shell.SyncWorkspaces(scope.Id, [saved], saved.Id, scope.Active.Schema!);

        var restored = Assert.IsType<RecordTab>(Assert.Single(_app.Shell.AllWorkspaces.Single(w => w.WorkspaceId == saved.Id).Tabs));
        Assert.Equal(TestApp.Kunden.Ref, restored.Table.Ref);
        Assert.Equal([["4711"]], restored.Diagram.Keys);
        Assert.Equal([step.Nodes], restored.Diagram.Steps.Select(s => s.Nodes));
    }

    [Fact]
    public void A_workspace_file_with_nulls_in_a_record_tab_reads_as_empty_lists()
    {
        var workspace = new Workspace(Guid.NewGuid(), Guid.NewGuid(), "W")
        {
            Tabs = [TabState.OfRecords(new RecordTabState("APP", "KUNDEN", false, null!, [new RecordStep(RecordStepKind.Expand, null!, DiagramSide.Parents)]))],
        };

        var records = workspace.Normalized().Tabs[0].Records!;

        Assert.Empty(records.Keys);
        Assert.Empty(Assert.Single(records.Steps).Nodes);
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();
}
