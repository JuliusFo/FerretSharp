using FerretSharp.Core.Schema;
using FerretSharp.Core.Settings;
using FerretSharp.Core.Workspaces;
using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>
/// WP-92 (ADR 0052): the dock on the right shows one panel at a time for the whole window (decisions of the user); form
/// and inspector show only beside a tab with a grid, the change overview and the help beside any.
/// </summary>
public sealed class DockTests : IAsyncDisposable
{
    private readonly TestApp _app = new();

    [Fact]
    public void A_panel_opens_replaces_the_open_one_and_closes_by_its_own_button_only()
    {
        var shell = _app.Shell;
        var changed = new List<DockPanel>();
        shell.DockChanged += changed.Add;

        shell.ToggleDock(DockPanel.Form);
        shell.ShowDock(DockPanel.Changes); // one at a time: the form goes
        shell.CloseDock(DockPanel.Form); // the form's ✕ from a view not re-rendered yet: the changes stay
        shell.ShowDock(DockPanel.Changes); // nothing new: no event, nothing saved
        shell.ToggleDock(DockPanel.Changes);

        Assert.Equal([DockPanel.Form, DockPanel.Changes, DockPanel.None], changed);
        Assert.Equal(DockPanel.None, shell.Dock);
        Assert.False(shell.ChangesOpen);
    }

    /// <summary>The settings of a newer version may name a panel this one does not know: the dock starts closed.</summary>
    [Theory]
    [InlineData(DockPanel.Inspector, DockPanel.Inspector)]
    [InlineData((DockPanel)42, DockPanel.None)]
    public void The_dock_starts_as_it_was_left(DockPanel saved, DockPanel shown)
    {
        _app.Shell.RestoreDock(saved);

        Assert.Equal(shown, _app.Shell.Dock);
    }

    [Theory]
    [InlineData("data", DockPanel.Inspector, true)]
    [InlineData("data", DockPanel.Form, true)]
    [InlineData("columns", DockPanel.Inspector, false)] // a table's other views have no grid beside them
    [InlineData("columns", DockPanel.Form, false)]
    [InlineData("sql", DockPanel.Form, true)] // beside the result
    [InlineData("sql", DockPanel.Inspector, false)]
    [InlineData("plsql", DockPanel.Form, false)]
    [InlineData("plsql", DockPanel.Changes, true)]
    [InlineData("none", DockPanel.Help, true)]
    [InlineData("none", DockPanel.Form, false)]
    public void Form_and_inspector_show_only_beside_a_grid_the_other_panels_beside_any_tab(string tab, DockPanel panel, bool shows)
    {
        var workspace = Guid.NewGuid();
        WorkspaceTab? active = tab switch
        {
            "data" => new TableTab(workspace, TestApp.Kunden),
            "columns" => new TableTab(workspace, TestApp.Kunden) { Mode = TabMode.Structure },
            "sql" => new SqlTab(workspace, "SQL 1"),
            "plsql" => new PlSqlTab(workspace, new PlSqlObjectSummary("APP_USER", "PKG_KUNDEN", PlSqlKind.Package, "VALID")),
            _ => null,
        };

        Assert.Equal(shows, DockPanels.ShowsIn(panel, active));
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();
}
