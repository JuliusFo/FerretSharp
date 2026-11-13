using FerretSharp.Core.Data;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;
using FerretSharp.Core.Scripts;
using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>WP-84: a script started from a cell – read again, values put in, opened (and run) in the table tab's workspace.</summary>
public sealed class ScriptCellLaunchTests : IAsyncDisposable
{
    private readonly TestApp _app = new();
    private readonly TestFolder _folder = new();

    private static readonly ColumnInfo Id = new("CUSTOMER_ID", "NUMBER", null, false, 10, 0, true, false, null, 1);

    public async ValueTask DisposeAsync()
    {
        await _app.DisposeAsync();
        _folder.Dispose();
    }

    private ScriptRoot Root => new(ScriptRootKind.Personal, _folder.Path);

    private ScriptUsage Usage => _usage ??= new ScriptUsage(_folder.Combine("usage.json"));

    private ScriptUsage? _usage;

    private ScriptMatch Write(string name, string text)
    {
        File.WriteAllText(_folder.Combine(name), text);
        var parsed = ScriptHeaders.Parse(text);
        var entry = new ScriptEntry(Root, name, parsed.Header.Description, parsed.Header.Parameters, [], DateTime.UnixEpoch, text.Length)
        {
            Targets = parsed.Header.Targets,
        };
        return new ScriptMatch(entry, parsed.Header.Targets[0]);
    }

    [Fact]
    public async Task A_query_runs_at_once_in_a_tab_linked_to_its_file_in_the_tabs_workspace()
    {
        await _app.OpenAsync(TestApp.Profile("Test"));
        var workspace = _app.Shell.ActiveWorkspace!.WorkspaceId;
        var match = Write("orders.sql", "-- @param id number\n-- @target id CUSTOMER.ID\n\nSELECT * FROM orders WHERE customer_id = :id");

        var (start, refusal) = await ScriptCellLaunch.PrepareAsync(workspace, match, Id, [4711m], Usage, TestContext.Current.CancellationToken);
        Assert.Null(refusal);
        Assert.False(start!.AsksFirst);
        var tab = await ScriptCellLaunch.StartAsync(_app.Shell, Usage, start, null, run: true, TestContext.Current.CancellationToken);

        Assert.NotNull(tab);
        // The target shows above the SQL (fix-script-header-in-editor); the parameter is a variable.
        Assert.Equal(("orders", "-- @target id CUSTOMER.ID\n\nSELECT * FROM orders WHERE customer_id = :id"), (tab.Title, tab.Text));
        Assert.False(tab.ScriptChanged);
        Assert.Equal([new SqlVariable("id", SqlVariableType.Number, "4711")], tab.Variables);
        Assert.Equal("orders.sql", tab.Script?.Path);
        Assert.True(tab.RunOnOpen);
        Assert.Same(tab, _app.Shell.ActiveTab);
        Assert.Equal(match.Script, Usage.Ordered([Write("other.sql", "-- @param id number\n-- @target id CUSTOMER.ID\nSELECT :id FROM dual"), match])[0].Script);
    }

    [Fact]
    public async Task Starting_again_takes_the_linked_tab_and_sets_its_values_but_keeps_its_others()
    {
        await _app.OpenAsync(TestApp.Profile("Test"));
        var workspace = _app.Shell.ActiveWorkspace!.WorkspaceId;
        var match = Write("orders.sql", "-- @param id number\n-- @target id CUSTOMER.ID\n\nSELECT * FROM orders WHERE customer_id = :id AND x = :x");
        var (first, _) = await ScriptCellLaunch.PrepareAsync(workspace, match, Id, [1m], Usage, TestContext.Current.CancellationToken);
        var tab = (await ScriptCellLaunch.StartAsync(_app.Shell, Usage, first!, null, run: false, TestContext.Current.CancellationToken))!;
        tab.Variables = [.. tab.Variables, new SqlVariable("x", SqlVariableType.Text, "kept")];
        _app.Shell.OpenSql();

        var (second, _) = await ScriptCellLaunch.PrepareAsync(workspace, match, Id, [2m], Usage, TestContext.Current.CancellationToken);
        var again = await ScriptCellLaunch.StartAsync(_app.Shell, Usage, second!, null, run: true, TestContext.Current.CancellationToken);

        Assert.Same(tab, again);
        Assert.Same(tab, _app.Shell.ActiveTab);
        Assert.Equal([new SqlVariable("id", SqlVariableType.Number, "2"), new SqlVariable("x", SqlVariableType.Text, "kept")], tab.Variables);
        Assert.True(tab.RunOnOpen);
    }

    /// <summary>Review WP-84, S1: the linked tab ran its own edited text – an UPDATE added to it – because the file only reads.</summary>
    [Fact]
    public async Task A_linked_tab_with_another_text_is_left_alone_and_a_new_tab_runs_the_files_text()
    {
        await _app.OpenAsync(TestApp.Profile("Test"));
        var workspace = _app.Shell.ActiveWorkspace!.WorkspaceId;
        var match = Write("orders.sql", "-- @param id number\n-- @target id CUSTOMER.ID\n\nSELECT * FROM orders WHERE customer_id = :id");
        var (first, _) = await ScriptCellLaunch.PrepareAsync(workspace, match, Id, [1m], Usage, TestContext.Current.CancellationToken);
        var edited = (await ScriptCellLaunch.StartAsync(_app.Shell, Usage, first!, null, run: false, TestContext.Current.CancellationToken))!;
        edited.Text += ";\nUPDATE orders SET note = 'x' WHERE customer_id = :id";

        var (second, _) = await ScriptCellLaunch.PrepareAsync(workspace, match, Id, [2m], Usage, TestContext.Current.CancellationToken);
        var tab = (await ScriptCellLaunch.StartAsync(_app.Shell, Usage, second!, null, run: true, TestContext.Current.CancellationToken))!;

        Assert.NotSame(edited, tab);
        Assert.False(edited.RunOnOpen);
        Assert.Equal([new SqlVariable("id", SqlVariableType.Number, "1")], edited.Variables);
        Assert.Equal("SELECT * FROM orders WHERE customer_id = :id", tab.Text);
        Assert.Null(tab.Script); // one tab per file stays linked
        Assert.True(tab.RunOnOpen);
    }

    [Fact]
    public async Task A_list_of_values_opens_an_unlinked_tab_and_a_script_that_writes_only_opens()
    {
        await _app.OpenAsync(TestApp.Profile("Test"));
        var workspace = _app.Shell.ActiveWorkspace!.WorkspaceId;
        var list = Write("many.sql", "-- @param ids number list\n-- @target ids CUSTOMER.ID\n\nSELECT * FROM orders WHERE customer_id IN (:ids)");
        var writes = Write("fix.sql", "-- @param id number\n-- @target id CUSTOMER.ID\n-- @param note text\n\nUPDATE orders SET note = :note WHERE customer_id = :id");

        var (many, _) = await ScriptCellLaunch.PrepareAsync(workspace, list, Id, [1m, 2m, 1m], Usage, TestContext.Current.CancellationToken);
        var manyTab = (await ScriptCellLaunch.StartAsync(_app.Shell, Usage, many!, null, run: true, TestContext.Current.CancellationToken))!;
        var (fix, _) = await ScriptCellLaunch.PrepareAsync(workspace, writes, Id, [1m], Usage, TestContext.Current.CancellationToken);
        var fixTab = (await ScriptCellLaunch.StartAsync(_app.Shell, Usage, fix!, null, run: true, TestContext.Current.CancellationToken))!;

        Assert.Equal("SELECT * FROM orders WHERE customer_id IN (:ids_1, :ids_2)", manyTab.Text);
        Assert.Null(manyTab.Script); // saving must not write the spread binds into the shared file
        Assert.True(manyTab.RunOnOpen);
        Assert.False(fix!.AsksFirst); // a script that writes asks nothing: it only opens
        Assert.False(fixTab.RunOnOpen);
        Assert.NotNull(fixTab.Script);
    }

    [Fact]
    public async Task The_dialogs_values_go_into_the_tab_and_come_back_next_time()
    {
        await _app.OpenAsync(TestApp.Profile("Test"));
        var workspace = _app.Shell.ActiveWorkspace!.WorkspaceId;
        var match = Write("since.sql", "-- @param id number\n-- @target id CUSTOMER.ID\n-- @param since date\n\nSELECT * FROM orders WHERE customer_id = :id AND created >= :since");

        var (start, _) = await ScriptCellLaunch.PrepareAsync(workspace, match, Id, [1m], Usage, TestContext.Current.CancellationToken);
        Assert.True(start!.AsksFirst);
        var tab = (await ScriptCellLaunch.StartAsync(_app.Shell, Usage, start, [new SqlVariable("since", SqlVariableType.Date, "2026-01-01")], run: false, TestContext.Current.CancellationToken))!;
        var (next, _) = await ScriptCellLaunch.PrepareAsync(workspace, match, Id, [2m], Usage, TestContext.Current.CancellationToken);

        Assert.Equal("2026-01-01", tab.Variables.Single(v => v.Name == "since").Value);
        Assert.False(tab.RunOnOpen); // "Open in editor"
        Assert.Equal("2026-01-01", next!.Run.Variables.Single(v => v.Name == "since").Value);
    }

    [Fact]
    public async Task A_missing_file_and_a_null_cell_are_refused_and_the_rows_values_start_with_the_clicked_one()
    {
        await _app.OpenAsync(TestApp.Profile("Test"));
        var workspace = _app.Shell.ActiveWorkspace!.WorkspaceId;
        var match = Write("gone.sql", "-- @param id number\n-- @target id CUSTOMER.ID\nSELECT :id FROM dual");
        var (_, nullCell) = await ScriptCellLaunch.PrepareAsync(workspace, match, Id, [null], Usage, TestContext.Current.CancellationToken);
        File.Delete(_folder.Combine("gone.sql"));

        var (start, missing) = await ScriptCellLaunch.PrepareAsync(workspace, match, Id, [1m], Usage, TestContext.Current.CancellationToken);

        Assert.NotNull(nullCell);
        Assert.Null(start);
        Assert.NotNull(missing);
        var clicked = new RowData(new RowKey.RowId("A"), [2m]);
        Assert.Equal([2m, 1m], ScriptCellLaunch.ValuesOf(clicked, [new RowData(new RowKey.RowId("B"), [1m]), clicked], 0));
    }
}
