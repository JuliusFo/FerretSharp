using FerretSharp.Core.Schema;
using FerretSharp.Core.Workspaces;
using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>PL/SQL tabs (WP-28): opening, jumping to a line, saving and restoring, dropping vanished units.</summary>
public sealed class PlSqlTabTests : IAsyncDisposable
{
    private readonly TestApp _app = new();

    [Fact]
    public async Task Opening_a_unit_twice_activates_its_tab()
    {
        await _app.OpenAsync(TestApp.Profile("Test"));
        var sql = _app.Shell.OpenSql()!;

        var tab = _app.Shell.OpenPlSql(TestApp.Rechnung)!;
        _app.Shell.ActivateTab(sql);
        var again = _app.Shell.OpenPlSql(TestApp.Rechnung, PlSqlView.Parameters);

        Assert.Same(tab, again);
        Assert.Same(tab, _app.Shell.ActiveTab);
        Assert.Equal(PlSqlView.Parameters, tab.View);
        Assert.Single(_app.Shell.Tabs.OfType<PlSqlTab>());
    }

    [Fact]
    public async Task Opening_at_a_line_shows_the_part_and_leaves_the_position_for_the_source_view()
    {
        await _app.OpenAsync(TestApp.Profile("Test"));

        var tab = _app.Shell.OpenPlSql(TestApp.Rechnung, at: new SourcePosition(PlSqlPart.Body, 120, 7))!;

        Assert.Equal(PlSqlView.Body, tab.View);
        Assert.Equal(new SourcePosition(PlSqlPart.Body, 120, 7), tab.Reveal);
    }

    [Fact]
    public void A_view_the_kind_does_not_have_is_not_chosen()
    {
        var trigger = new PlSqlObjectSummary("APP", "TRG", PlSqlKind.Trigger, "VALID");
        var procedure = new PlSqlObjectSummary("APP", "P", PlSqlKind.Procedure, "VALID");

        Assert.Equal([PlSqlView.Source, PlSqlView.Errors, PlSqlView.Dependencies], new PlSqlTab(Guid.NewGuid(), trigger).Views);
        Assert.DoesNotContain(PlSqlView.Body, new PlSqlTab(Guid.NewGuid(), procedure).Views);
        Assert.Equal(PlSqlView.Source, PlSqlTab.Restore(Guid.NewGuid(), trigger, new PlSqlTabState("APP", "TRG", PlSqlKind.Trigger, PlSqlView.Parameters)).View);
    }

    [Fact]
    public async Task Tabs_are_saved_and_restored_while_the_unit_exists()
    {
        var scope = await _app.OpenAsync(TestApp.Profile("Test"));
        var tab = _app.Shell.OpenPlSql(TestApp.Rechnung, PlSqlView.Body)!;
        var state = tab.ToState(_app.Shell.Tabs);
        Assert.Equal(new PlSqlTabState("APP_USER", "PKG_RECHNUNG", PlSqlKind.Package, PlSqlView.Body), state.PlSql);

        var saved = new Workspace(Guid.NewGuid(), scope.Id, "Gespeichert")
        {
            Tabs = [state, TabState.OfPlSql(new PlSqlTabState("APP_USER", "PKG_WEG", PlSqlKind.Package, PlSqlView.Source))],
            ActiveTabIndex = 0,
        };
        _app.Shell.SyncWorkspaces(scope.Id, [saved], saved.Id, scope.Active.Schema!);

        var restored = Assert.IsType<PlSqlTab>(Assert.Single(_app.Shell.AllWorkspaces.Single(w => w.WorkspaceId == saved.Id).Tabs));
        Assert.Equal((TestApp.Rechnung.Ref, PlSqlView.Body), (restored.Unit.Ref, restored.View));
    }

    [Fact]
    public async Task A_schema_refresh_drops_tabs_of_units_that_are_gone()
    {
        var scope = await _app.OpenAsync(TestApp.Profile("Test"));
        var tab = _app.Shell.OpenPlSql(TestApp.Rechnung)!;
        var table = _app.Shell.OpenTable(TestApp.Kunden)!;

        _app.PlSqlObjects = [];
        await scope.Active.Schema!.RefreshAsync(TestContext.Current.CancellationToken);
        _app.Shell.RemoveVanishedTabs(scope.Id, scope.Active.Schema!);

        Assert.DoesNotContain(tab, _app.Shell.Tabs);
        Assert.Contains(table, _app.Shell.Tabs);
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();
}

public class ExplorerListTests
{
    private static readonly PlSqlObjectSummary Rechnung = new("APP", "PKG_RECHNUNG", PlSqlKind.Package, "VALID");
    private static readonly PlSqlObjectSummary Druck = new("ERP", "PKG_DRUCK", PlSqlKind.Package, "VALID", null, new SynonymInfo("APP", "DRUCK"));
    private static readonly PlSqlObjectSummary Import = new("APP", "1_IMPORT", PlSqlKind.Procedure, "VALID");

    [Fact]
    public void Units_are_found_by_shown_and_real_name_ignoring_case()
    {
        Assert.Equal([Druck], ExplorerList.Filter([Rechnung, Druck, Import], " druck "));
        Assert.Equal([Druck], ExplorerList.Filter([Rechnung, Druck, Import], "pkg_d"));
        Assert.Equal(3, ExplorerList.Filter([Rechnung, Druck, Import], "").Count());
    }

    [Fact]
    public void Groups_by_first_letter_with_other_names_last()
    {
        var groups = ExplorerList.Group([Druck, Import, Rechnung], u => u.DisplayName);

        Assert.Equal(['D', 'P', '#'], groups.Keys);
        Assert.Equal([Import], groups['#']);
    }

    [Theory]
    [InlineData("  RETURN p_betrag * 1.19;", "P_BETRAG", 10)]
    [InlineData("x := 1;", "nicht da", 1)]
    public void A_hit_points_at_the_searched_text(string line, string text, int column) =>
        Assert.Equal(column, SourceSearch.ColumnOf(line, text));

    [Fact]
    public void Hits_are_grouped_by_unit_and_part_in_order()
    {
        var spec = new PlSqlRef("APP", "PKG_RECHNUNG", PlSqlKind.Package);
        var proc = new PlSqlRef("APP", "P", PlSqlKind.Procedure);
        SourceHit[] hits = [new(proc, PlSqlPart.Spec, 3, "a"), new(spec, PlSqlPart.Body, 1, "b"), new(spec, PlSqlPart.Body, 9, "c"), new(spec, PlSqlPart.Spec, 2, "d")];

        var groups = SourceSearch.Group(hits);

        Assert.Equal([(proc, PlSqlPart.Spec), (spec, PlSqlPart.Body), (spec, PlSqlPart.Spec)], groups.Select(g => g.Key));
        Assert.Equal([1, 9], groups[1].Select(h => h.Line));
    }
}

public class PlSqlMarkersTests
{
    [Fact]
    public void Errors_of_the_part_underline_the_word_they_point_at()
    {
        string[] lines = ["PACKAGE BODY P AS", "  PROCEDURE LAUF IS", "  BEGIN", "    NULL; X_UNBEKANNT := 1;", "  END;", "END;"];
        PlSqlError[] errors =
        [
            new(PlSqlPart.Body, 4, 11, "PLS-00201: identifier 'X_UNBEKANNT' must be declared", false),
            new(PlSqlPart.Body, 4, 11, "PL/SQL: Statement ignored", false),
            new(PlSqlPart.Spec, 1, 1, "PLW-05018: no AUTHID clause", true),
        ];

        var markers = PlSqlMarkers.For(errors, PlSqlPart.Body, lines);

        Assert.Equal(2, markers.Count);
        Assert.Equal((4, 11, 4, 22, "error"), (markers[0].Line, markers[0].Column, markers[0].EndLine, markers[0].EndColumn, markers[0].Severity));
        Assert.Equal("warning", Assert.Single(PlSqlMarkers.For(errors, PlSqlPart.Spec, lines)).Severity);
    }

    [Fact]
    public void Positions_outside_the_text_stay_within_it()
    {
        var marker = Assert.Single(PlSqlMarkers.For([new PlSqlError(PlSqlPart.Spec, 99, 50, "x", false)], PlSqlPart.Spec, ["END;"]));

        Assert.Equal((1, 5, 6), (marker.Line, marker.Column, marker.EndColumn));
    }
}
