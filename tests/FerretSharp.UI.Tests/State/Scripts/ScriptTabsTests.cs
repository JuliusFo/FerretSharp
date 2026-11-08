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
        Assert.Empty(tab.Runs);
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
