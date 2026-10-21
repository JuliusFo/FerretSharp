using FerretSharp.Core.Schema;
using FerretSharp.Core.Settings;
using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>
/// When the global actions can run (WP-38): the same rule for their shortcuts and the command palette, which greys out
/// the rest with the reason.
/// </summary>
public sealed class ShellActionsTests
{
    private static readonly Guid Workspace = Guid.NewGuid();

    private static readonly TableTab Table = new(Workspace, new TableSummary("APP", "KUNDEN", TableKind.Table));

    /// <summary>Connected, the explorer shown, a table tab active, writable – everything that needs no more can run.</summary>
    private static readonly ShellActionContext Ready = new(
        InExplorer: true, Connected: true, ActiveTab: Table, Writable: true, LinkedProject: false, CanGoBack: false, CanGoForward: false, PreviousConnection: false);

    private static readonly ShellActionContext Disconnected = Ready with { Connected = false, ActiveTab = null, Writable = false };

    [Fact]
    public void The_palette_lists_the_global_actions_without_its_own_and_without_the_grids()
    {
        var actions = ShellActions.InPalette.Select(d => d.Action).ToList();

        Assert.Contains(ShortcutAction.Flush, actions);
        Assert.Contains(ShortcutAction.ConnectionSwitcher, actions);
        Assert.DoesNotContain(ShortcutAction.QuickOpen, actions);
        Assert.DoesNotContain(ShortcutAction.CommandPalette, actions);
        Assert.DoesNotContain(ShortcutAction.UndoEdit, actions);
        Assert.DoesNotContain(ShortcutAction.ToggleForm, actions);
        Assert.Equal(ShortcutMap.Definitions.Select(d => d.Action).Where(actions.Contains), actions); // settings order
    }

    [Fact]
    public void Every_listed_action_has_a_rule_and_runs_when_everything_is_there()
    {
        var all = Ready with { LinkedProject = true, CanGoBack = true, CanGoForward = true, PreviousConnection = true, ActiveTab = new SqlTab(Workspace, "SQL 1") };

        Assert.All(ShellActions.InPalette, d => Assert.Null(ShellActions.Unavailable(d.Action, all)));
    }

    [Theory]
    [InlineData(ShortcutAction.ConnectionSwitcher)]
    [InlineData(ShortcutAction.CommandPalette)]
    public void Without_a_connection_only_choosing_one_and_the_palette_run(ShortcutAction action) =>
        Assert.Null(ShellActions.Unavailable(action, Disconnected));

    [Fact]
    public void Without_a_connection_the_rest_says_so()
    {
        Assert.All(
            ShellActions.InPalette.Where(d => d.Action is not (ShortcutAction.ConnectionSwitcher or ShortcutAction.PreviousConnection)),
            d => Assert.Equal("Keine Verbindung aktiv.", ShellActions.Unavailable(d.Action, Disconnected)));
        Assert.Equal("Keine Verbindung aktiv.", ShellActions.Unavailable(ShortcutAction.QuickOpen, Disconnected));
    }

    [Fact]
    public void Tab_actions_need_the_explorer_and_a_tab()
    {
        Assert.Equal("Nur im Explorer.", ShellActions.Unavailable(ShortcutAction.CloseTab, Ready with { InExplorer = false }));
        Assert.Equal("Kein Tab offen.", ShellActions.Unavailable(ShortcutAction.Refresh, Ready with { ActiveTab = null }));
        Assert.Null(ShellActions.Unavailable(ShortcutAction.PinTab, Ready));
    }

    [Fact]
    public void Running_a_script_needs_a_sql_tab()
    {
        Assert.Equal("Nur in einem SQL-Tab.", ShellActions.Unavailable(ShortcutAction.RunScript, Ready));
        Assert.Null(ShellActions.Unavailable(ShortcutAction.RunScript, Ready with { ActiveTab = new SqlTab(Workspace, "SQL 1") }));
    }

    [Fact]
    public void Writing_and_committing_need_a_writable_workspace()
    {
        var readOnly = Ready with { Writable = false };

        Assert.Equal("Der Workspace ist schreibgeschützt.", ShellActions.Unavailable(ShortcutAction.Flush, readOnly));
        Assert.Equal("Der Workspace ist schreibgeschützt.", ShellActions.Unavailable(ShortcutAction.Commit, readOnly));
        Assert.Null(ShellActions.Unavailable(ShortcutAction.Flush, Ready));
    }

    [Fact]
    public void Back_forward_linq_and_the_previous_connection_need_their_target()
    {
        Assert.Equal("Kein FK-Sprung, von dem es zurückgeht.", ShellActions.Unavailable(ShortcutAction.Back, Ready));
        Assert.Equal("Nichts, wohin es vorwärtsgeht.", ShellActions.Unavailable(ShortcutAction.Forward, Ready));
        Assert.Equal("Kein C#-Projekt verknüpft.", ShellActions.Unavailable(ShortcutAction.NewLinq, Ready));
        Assert.Equal("Keine vorige Verbindung offen.", ShellActions.Unavailable(ShortcutAction.PreviousConnection, Ready));
        Assert.Null(ShellActions.Unavailable(ShortcutAction.NewSql, Ready with { ActiveTab = null }));
    }

    [Theory]
    [InlineData(ShortcutAction.UndoEdit)]
    [InlineData(ShortcutAction.ToggleForm)]
    public void The_grids_own_actions_are_no_global_ones(ShortcutAction action) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ShellActions.Unavailable(action, Ready));

    /// <summary>Review of WP-38: the palette greys out what the active tab's view does not handle.</summary>
    [Fact]
    public void Tab_commands_only_where_the_tab_handles_them()
    {
        var plSql = Ready with { ActiveTab = new PlSqlTab(Workspace, new PlSqlObjectSummary("APP", "PKG_KUNDE", PlSqlKind.Package, "VALID")) };
        var newTable = Ready with { ActiveTab = new NewTableTab(Workspace, "APP") };
        var linq = Ready with { ActiveTab = new LinqTab(Workspace, "LINQ 1") };

        Assert.Equal("Nicht in diesem Tab.", ShellActions.Unavailable(ShortcutAction.ApplyFilters, plSql));
        Assert.Null(ShellActions.Unavailable(ShortcutAction.Refresh, plSql));
        Assert.Null(ShellActions.Unavailable(ShortcutAction.FindColumn, plSql));
        Assert.All(
            [ShortcutAction.ApplyFilters, ShortcutAction.Refresh, ShortcutAction.FindColumn],
            a => Assert.Equal("Nicht in diesem Tab.", ShellActions.Unavailable(a, newTable)));
        Assert.Null(ShellActions.Unavailable(ShortcutAction.ApplyFilters, linq));
        Assert.Null(ShellActions.Unavailable(ShortcutAction.CloseTab, newTable));
    }
}
