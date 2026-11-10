using FerretSharp.Core.Query;
using FerretSharp.Core.Scripts;
using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>WP-77: a script opens in a SQL tab linked to its file; one from the library goes into the active SQL tab.</summary>
public sealed class ScriptTabsTests : IAsyncDisposable
{
    private readonly TestApp _app = new();

    private static readonly ScriptRoot Personal = new(ScriptRootKind.Personal, Path.Combine(Path.GetTempPath(), "scripts"));

    private static ScriptDocument Document(string body, params ScriptParameter[] parameters) =>
        new(new ParsedScript(new ScriptHeader("Open orders", parameters, []), body, []), "HASH");

    [Fact]
    public async Task A_script_opens_in_a_sql_tab_named_after_it_with_its_parameters_and_nothing_runs()
    {
        await _app.OpenAsync(TestApp.Profile("Test"));

        var tab = _app.Shell.OpenScript(Personal, "Orders/Open orders.sql", Document("SELECT * FROM orders WHERE id = :id", new ScriptParameter("id", SqlVariableType.Number)))!;

        Assert.Equal(("Open orders", "SELECT * FROM orders WHERE id = :id"), (tab.Title, tab.Text));
        Assert.Equal([new SqlVariable("id", SqlVariableType.Number, "")], tab.Variables);
        Assert.Equal((ScriptRootKind.Personal, "Orders/Open orders.sql", "HASH"), (tab.Script!.Root, tab.Script.Path, tab.Script.FileHash));
        Assert.False(tab.ScriptChanged);
        Assert.Empty(tab.Results.All);
        Assert.Same(tab, _app.Shell.ActiveTab);
    }

    [Fact]
    public async Task Opening_the_same_script_again_shows_its_tab()
    {
        await _app.OpenAsync(TestApp.Profile("Test"));
        var first = _app.Shell.OpenScript(Personal, "a.sql", Document("SELECT 1 FROM dual"))!;
        _app.Shell.OpenSql();

        var again = _app.Shell.OpenScript(Personal, "A.SQL", Document("SELECT 1 FROM dual"));

        Assert.Same(first, again);
        Assert.Same(first, _app.Shell.ActiveTab);
        Assert.Single(_app.Shell.Tabs.OfType<SqlTab>(), t => t.Script is not null);
    }

    [Fact]
    public async Task The_dot_follows_text_and_parameter_types_and_the_link_is_saved_with_the_tab()
    {
        await _app.OpenAsync(TestApp.Profile("Test"));
        var tab = _app.Shell.OpenScript(Personal, "a.sql", Document("SELECT :id FROM dual", new ScriptParameter("id", SqlVariableType.Number)))!;

        tab.Variables = [new SqlVariable("id", SqlVariableType.Number, "4711")]; // a value is no change of the script
        Assert.False(tab.ScriptChanged);
        tab.Text += " -- for one run";
        Assert.True(tab.ScriptChanged);

        var state = tab.ToState([tab]);
        var restored = SqlTab.Restore(Guid.NewGuid(), state.Sql!);
        Assert.Equal(tab.Script, restored.Script);
        Assert.True(restored.ScriptChanged);
    }

    /// <summary>Review WP-77, M1 (tab side): a file without header shows no dot once its binds have variables.</summary>
    [Fact]
    public async Task A_file_without_header_is_not_changed_after_opening()
    {
        await _app.OpenAsync(TestApp.Profile("Test"));
        var tab = _app.Shell.OpenScript(Personal, "a.sql", new ScriptDocument(new ParsedScript(ScriptHeader.Empty, "SELECT :id FROM dual", []), "HASH"))!;

        tab.Variables = [new SqlVariable("id", SqlVariableType.Number, "")]; // what the view suggests for the bind

        Assert.False(tab.ScriptChanged);
    }

    /// <summary>
    /// Review WP-77, K2: a link without path or header (a workspace file edited by hand) broke the tab bar's render; the
    /// tab is restored without it. A long script name fits the tab title (K5).
    /// </summary>
    [Fact]
    public async Task An_incomplete_link_is_dropped_and_a_long_name_fits_the_title()
    {
        var state = new FerretSharp.Core.Workspaces.SqlTabState("x", "SELECT 1 FROM dual", [])
        {
            Script = new ScriptLink(ScriptRootKind.Personal, null, null!, "F", "S", null!),
        };
        var nullLine = state with { Script = new ScriptLink(ScriptRootKind.Personal, null, "a.sql", "F", "S", new ScriptHeader(null, [], [null!])) };

        var restored = SqlTab.Restore(Guid.NewGuid(), state);

        Assert.Null(restored.Script);
        Assert.Null(SqlTab.Restore(Guid.NewGuid(), nullLine).Script); // second review pass: a null line broke the next save
        Assert.False(restored.ScriptChanged);

        await _app.OpenAsync(TestApp.Profile("Test"));
        var tab = _app.Shell.OpenScript(Personal, new string('n', 60) + ".sql", Document("SELECT 1 FROM dual"))!;
        Assert.Equal(FerretSharp.Core.Workspaces.Workspace.MaxNameLength, tab.Title.Length);
    }

    /// <summary>
    /// A script tab without changes of its own closes without asking – its text is in the file (decision of the user,
    /// E2E of WP-77: the question for every SQL tab with text came up); with changes it asks as before.
    /// </summary>
    [Fact]
    public async Task A_script_tab_asks_before_closing_only_with_changes_of_its_own()
    {
        await _app.OpenAsync(TestApp.Profile("Test"));
        var tab = _app.Shell.OpenScript(Personal, "a.sql", Document("SELECT 1 FROM dual"))!;

        Assert.Null(CloseTabQuestion.For(tab, typedValue: false));
        // Keys the editor has not reported yet (its debounce): the tab cannot know whether they change it (review WP-77).
        Assert.Equal(CloseTabLoss.EditorText, CloseTabQuestion.For(tab, typedValue: true)?.Loss);
        tab.Text += " -- for one run";
        Assert.Equal(CloseTabLoss.EditorText, CloseTabQuestion.For(tab, typedValue: false)?.Loss);
    }

    [Fact]
    public async Task Insert_goes_to_the_active_sql_tab_only()
    {
        await _app.OpenAsync(TestApp.Profile("Test"));
        var entry = new ScriptEntry(Personal, "a.sql", null, [], [], DateTime.UtcNow, 10);
        _app.Shell.OpenTable(TestApp.Kunden);
        Assert.False(_app.Shell.InsertScript(entry));

        var sql = _app.Shell.OpenSql()!;
        var commands = new List<(WorkspaceTab, TabCommand)>();
        _app.Shell.TabCommandRequested += (t, c) => commands.Add((t, c));

        Assert.True(_app.Shell.InsertScript(entry));
        Assert.Same(entry, sql.PendingInsert);
        Assert.Equal([(sql, TabCommand.InsertScript)], commands);
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();
}
