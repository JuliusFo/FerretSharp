using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Schema;
using FerretSharp.UI.State;
using NSubstitute;

namespace FerretSharp.UI.Tests.State;

/// <summary>WP-72: what the report page shows of a report – filters, groups, first rows, the sections open for the session.</summary>
public sealed class ReportViewTests
{
    private const string Owner = "SHOP";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ColumnInfo Column(string name, int position, bool nullable = true) =>
        new(name, "NUMBER", null, false, 10, 0, nullable, false, null, position);

    private static PropertyExport Prop(string name, string column) => new(name, "int", false, false, column, null, null, null, false, null);

    /// <summary>
    /// KUNDE and AUFTRAG mapped (AUFTRAG with three columns without property, one of them NOT NULL); without entity: GUTSCHEIN
    /// (FK to KUNDE), IMPORT_TMP (excepted), LOG and a view. AUFTRAG → KUNDE only in the model, LOG → GUTSCHEIN declared.
    /// </summary>
    private static async Task<(ModelReport Report, ClrModelMapping Mapping)> ReportAsync()
    {
        var reader = Substitute.For<ISchemaReader>();
        reader.GetTablesAsync(Owner, Arg.Any<CancellationToken>()).Returns(
        [
            new TableSummary(Owner, "AUFTRAG", TableKind.Table), new TableSummary(Owner, "KUNDE", TableKind.Table),
            new TableSummary(Owner, "GUTSCHEIN", TableKind.Table), new TableSummary(Owner, "IMPORT_TMP", TableKind.Table),
            new TableSummary(Owner, "LOG", TableKind.Table), new TableSummary(Owner, "V_UMSATZ", TableKind.View),
        ]);
        reader.GetSynonymTargetsAsync(Owner, Arg.Any<CancellationToken>()).Returns(SynonymTargets.None);
        reader.GetForeignKeysAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).Returns(
        [
            new ForeignKeyInfo("FK_GUTSCHEIN_KUNDE", new(Owner, "GUTSCHEIN"), ["KUNDE_ID"], new(Owner, "KUNDE"), ["KUNDE_ID"], FkSource.Declared),
            new ForeignKeyInfo("FK_LOG_GUTSCHEIN", new(Owner, "LOG"), ["GUTSCHEIN_ID"], new(Owner, "GUTSCHEIN"), ["GUTSCHEIN_ID"], FkSource.Declared),
        ]);
        reader.GetPrimaryKeysAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).Returns([new PrimaryKeyInfo(new(Owner, "KUNDE"), ["KUNDE_ID"])]);
        reader.GetColumnsAsync(Owner, Arg.Any<CancellationToken>()).Returns(new Dictionary<string, IReadOnlyList<ColumnInfo>>
        {
            ["AUFTRAG"] = [Column("AUFTRAG_ID", 1), Column("KUNDE_ID", 2), Column("RABATT", 3), Column("STORNO", 4, nullable: false), Column("NOTIZ", 5)],
            ["KUNDE"] = [Column("KUNDE_ID", 1)],
        });
        var schema = new SchemaCache(reader, Owner);
        await schema.LoadAsync(Ct);
        var model = new ModelExport(4, "8.0.11", "Shop.ShopContext", "options", null,
        [
            new EntityExport("Shop.Auftrag", "Shop.Auftrag", false, null, "AUFTRAG", null, null, null, [Prop("AuftragId", "AUFTRAG_ID"), Prop("KundeId", "KUNDE_ID")],
                ["AuftragId"], [new ForeignKeyExport(["KundeId"], "Shop.Kunde", ["KundeId"], "Kunde", null, false)]),
            new EntityExport("Shop.Kunde", "Shop.Kunde", false, null, "KUNDE", null, null, null, [Prop("KundeId", "KUNDE_ID")], ["KundeId"], []),
        ]);
        var mapping = await ClrModelMapping.BuildAsync(model, schema, Ct);
        return (ModelReport.Build(mapping, schema, ReportExceptions.Empty.With("IMPORT_*", "staging")), mapping);
    }

    [Fact]
    public async Task Tables_show_only_tables_by_default_and_the_chips_add_views_hidden_ones_or_narrow_to_linked_ones()
    {
        var (report, mapping) = await ReportAsync();
        var view = new ReportView();

        Assert.Equal(["GUTSCHEIN", "LOG"], view.Tables(report, mapping).Select(t => t.Table.Name));
        Assert.Equal((1, 1), ReportView.TableCounts(report));

        view.TableViews = true;
        view.TableHidden = true;
        Assert.Equal(["GUTSCHEIN", "IMPORT_TMP", "LOG", "V_UMSATZ"], view.Tables(report, mapping).Select(t => t.Table.Name));

        view.TableLinked = true; // LOG only references GUTSCHEIN, a table without entity
        Assert.Equal(["GUTSCHEIN"], view.Tables(report, mapping).Select(t => t.Table.Name));

        view.TableLinked = false;
        view.TableSearch = "umsatz";
        Assert.Equal(["V_UMSATZ"], view.Tables(report, mapping).Select(t => t.Table.Name));
    }

    [Fact]
    public async Task Columns_are_grouped_by_table_and_the_search_opens_the_groups_it_finds()
    {
        var (report, _) = await ReportAsync();
        var view = new ReportView();
        var auftrag = new TableRef(Owner, "AUFTRAG");

        var group = Assert.Single(view.ColumnGroups(report));
        Assert.Equal(["RABATT", "STORNO", "NOTIZ"], group.Columns.Select(c => c.Column.Name));
        Assert.False(view.IsGroupOpen(auftrag));

        view.ColumnSearch = "notiz";
        Assert.Equal(["NOTIZ"], view.Columns(report).Select(c => c.Column.Name));
        Assert.True(view.IsGroupOpen(auftrag));

        view.ColumnSearch = "";
        view.ColumnsRequired = true;
        Assert.Equal(["STORNO"], view.Columns(report).Select(c => c.Column.Name));

        view.OpenGroups(view.ColumnGroups(report));
        Assert.True(view.AnyGroupOpen && view.IsGroupOpen(auftrag));
        view.CloseGroups();
        Assert.False(view.IsGroupOpen(auftrag));
    }

    [Fact]
    public async Task Relationships_only_in_the_model_can_be_narrowed_to_those_a_constraint_fits()
    {
        var (report, _) = await ReportAsync();
        var view = new ReportView { RelationsConstraintOnly = true };

        Assert.Equal(["Auftrag.Kunde"], view.ModelOnly(report).Select(r => r.Relation.Name));
        Assert.Empty(report.DbOnly); // LOG → GUTSCHEIN: neither table has an entity
    }

    [Fact]
    public void A_list_shows_its_first_rows_until_all_are_asked_for()
    {
        var view = new ReportView();
        var rows = Enumerable.Range(1, ReportView.FirstRows + 7).ToList();

        var first = view.Slice(ReportPart.Unused, rows);
        view.ShowAll(ReportPart.Unused);

        Assert.Equal((ReportView.FirstRows, 7), (first.Shown.Count, first.More));
        Assert.Equal((rows.Count, 0), (view.Slice(ReportPart.Unused, rows).Shown.Count, view.Slice(ReportPart.Unused, rows).More));
        Assert.Equal(ReportView.FirstRows, view.Slice(ReportPart.Tables, rows).Shown.Count); // per list
    }

    [Fact]
    public void Open_sections_are_kept_for_the_session_and_a_link_opens_its_section_once()
    {
        var first = new ReportView();
        var wasOpen = first.IsOpen(ReportPart.Mismatches);
        try
        {
            first.Open(ReportPart.Mismatches);
            Assert.True(new ReportView().IsOpen(ReportPart.Mismatches)); // the page opened again

            var shell = new ShellState();
            shell.ShowReport(ReportPart.Mismatches);
            Assert.Equal(ShellPage.Report, shell.Page);
            Assert.Equal(ReportPart.Mismatches, shell.TakeReportPart());
            Assert.Null(shell.TakeReportPart());
        }
        finally
        {
            if (!wasOpen)
            {
                first.Toggle(ReportPart.Mismatches);
            }
        }
    }
}
