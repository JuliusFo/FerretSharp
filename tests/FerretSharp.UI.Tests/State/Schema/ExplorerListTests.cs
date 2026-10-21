using FerretSharp.Core.Schema;
using FerretSharp.Core.Workspaces;
using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

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

    /// <summary>Review 3.44.0, B4: the explorer filters by the quick open's rule – underscores may be left out, C# names count.</summary>
    [Fact]
    public void The_explorer_finds_what_the_quick_open_finds()
    {
        var lieferOrt = new TableSummary("APP", "LIEFER_ORT", TableKind.Table);

        Assert.True(Core.Query.ObjectSearch.Matches(lieferOrt, "lieferort", null));
        Assert.True(Core.Query.ObjectSearch.Matches(lieferOrt, "", null));
        Assert.True(Core.Query.ObjectSearch.Matches(Kunden, "kunde", "Customer"));
        Assert.True(Core.Query.ObjectSearch.Matches(Kunden, "customer", "Customer"));
        Assert.False(Core.Query.ObjectSearch.Matches(Kunden, "auftrag", "Customer"));
        Assert.Equal([Rechnung], ExplorerList.Filter([Rechnung, Druck, Import], "pkgrechnung"));
    }

    private static readonly TableSummary Kunden = new("APP", "KUNDEN", TableKind.Table);
    private static readonly TableSummary KundenView = new("APP", "KUNDEN_V", TableKind.View);
    private static readonly TableSummary KundenMView = new("APP", "KUNDEN_MV", TableKind.MaterializedView);

    [Fact]
    public void Views_are_with_the_tables_unless_they_have_a_section_of_their_own()
    {
        TableSummary[] all = [Kunden, KundenView, KundenMView];

        Assert.Equal(all, all.Where(t => ExplorerList.IsIn(t, ExplorerSection.Tables, separateViews: false)));
        Assert.DoesNotContain(all, t => ExplorerList.IsIn(t, ExplorerSection.Views, separateViews: false));
        Assert.Equal([Kunden], all.Where(t => ExplorerList.IsIn(t, ExplorerSection.Tables, separateViews: true)));
        Assert.Equal([KundenView, KundenMView], all.Where(t => ExplorerList.IsIn(t, ExplorerSection.Views, separateViews: true)));
        Assert.DoesNotContain(all, t => ExplorerList.IsIn(t, ExplorerSection.PlSql, separateViews: true));
    }

    [Fact]
    public void The_views_section_is_only_in_the_switch_while_views_are_separated()
    {
        Assert.Equal([ExplorerSection.Tables, ExplorerSection.PlSql], ExplorerList.Sections(separateViews: false));
        Assert.Equal([ExplorerSection.Tables, ExplorerSection.Views, ExplorerSection.PlSql], ExplorerList.Sections(separateViews: true));
        Assert.Equal(ExplorerSection.Tables, ExplorerList.Shown(ExplorerSection.Views, separateViews: false));
        Assert.Equal(ExplorerSection.Views, ExplorerList.Shown(ExplorerSection.Views, separateViews: true));
        Assert.Equal(ExplorerSection.PlSql, ExplorerList.Shown(ExplorerSection.PlSql, separateViews: false));
    }

    [Fact]
    public void Hits_in_the_other_sections_are_named_in_the_order_of_the_switch()
    {
        var counts = new Dictionary<ExplorerSection, int> { [ExplorerSection.Tables] = 2, [ExplorerSection.Views] = 0, [ExplorerSection.PlSql] = 1 };

        Assert.Equal([(ExplorerSection.PlSql, 1)], ExplorerList.OtherHits(ExplorerSection.Tables, separateViews: true, s => counts[s]));
        Assert.Equal([(ExplorerSection.Tables, 2), (ExplorerSection.PlSql, 1)], ExplorerList.OtherHits(ExplorerSection.Views, separateViews: true, s => counts[s]));
        Assert.Equal([(ExplorerSection.Tables, 2)], ExplorerList.OtherHits(ExplorerSection.PlSql, separateViews: false, s => counts[s]));
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
