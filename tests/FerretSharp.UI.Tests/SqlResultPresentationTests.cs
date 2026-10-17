using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Data;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;
using FerretSharp.Core.Settings;
using FerretSharp.UI.State;
using NSubstitute;

namespace FerretSharp.UI.Tests;

/// <summary>WP-48: result columns traced back to their tables read like table columns – C# names, enum members, FK badges.</summary>
public sealed class SqlResultPresentationTests
{
    private const string Owner = "APP";

    private static readonly TableDetails Kunden = new(new TableSummary(Owner, "KUNDEN", TableKind.Table),
        [
            new("KUNDE_ID", "NUMBER", null, false, 10, 0, false, false, null, 1),
            new("NAME", "VARCHAR2", 100, true, null, null, false, false, null, 2),
            new("KUNDENART", "NUMBER", null, false, 2, 0, false, false, null, 3, Comment: "1 privat, 2 Gewerbe"),
        ],
        ["KUNDE_ID"], [], false);

    private static readonly TableDetails Auftrag = new(new TableSummary(Owner, "AUFTRAG", TableKind.Table),
        [new("AUFTRAG_ID", "NUMBER", null, false, 10, 0, false, false, null, 1), new("KUNDE_ID", "NUMBER", null, false, 10, 0, false, false, null, 2)],
        ["AUFTRAG_ID"], [], false);

    private static readonly ForeignKeyInfo AuftragKunde = new("FK_AUFTRAG_KUNDE", Auftrag.Table.Ref, ["KUNDE_ID"], Kunden.Table.Ref, ["KUNDE_ID"], FkSource.Declared);

    private const string Sql = """
        SELECT "a"."AUFTRAG_ID", "a"."KUNDE_ID", "k"."KUNDENART" AS "ART", 1 AS "ONE"
        FROM "AUFTRAG" "a" INNER JOIN "KUNDEN" "k" ON "a"."KUNDE_ID" = "k"."KUNDE_ID"
        """;

    private static ColumnInfo Number(string name, int position) => new(name, "NUMBER", null, false, null, null, true, false, null, position);

    private static readonly IReadOnlyList<SqlColumn> Columns =
    [
        new("AUFTRAG_ID", Number("AUFTRAG_ID", 1), new SqlColumnOrigin(null, "a", "AUFTRAG_ID")),
        new("KUNDE_ID", Number("KUNDE_ID", 2), new SqlColumnOrigin(null, "a", "KUNDE_ID")),
        new("ART", Number("ART", 3), new SqlColumnOrigin(null, "k", "KUNDENART")),
        new("ONE", Number("ONE", 4)),
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<SqlResultPresentation> PresentationAsync(ClrNameDisplay names)
    {
        var reader = Substitute.For<ISchemaReader>();
        reader.GetTablesAsync(Owner, Arg.Any<CancellationToken>()).Returns((IReadOnlyList<TableSummary>)[Kunden.Table, Auftrag.Table]);
        reader.GetSynonymTargetsAsync(Owner, Arg.Any<CancellationToken>()).Returns(SynonymTargets.None);
        reader.GetForeignKeysAsync(Owner, Arg.Any<CancellationToken>()).Returns((IReadOnlyList<ForeignKeyInfo>)[AuftragKunde]);
        reader.GetDetailsAsync(Arg.Is<TableSummary>(t => t.Name == "KUNDEN"), Arg.Any<CancellationToken>()).Returns(Kunden);
        reader.GetDetailsAsync(Arg.Is<TableSummary>(t => t.Name == "AUFTRAG"), Arg.Any<CancellationToken>()).Returns(Auftrag);
        reader.GetColumnsAsync(Owner, Arg.Any<CancellationToken>()).Returns((IReadOnlyDictionary<string, IReadOnlyList<ColumnInfo>>)
            new Dictionary<string, IReadOnlyList<ColumnInfo>> { ["KUNDEN"] = Kunden.Columns, ["AUFTRAG"] = Auftrag.Columns });
        var schema = new SchemaCache(reader, Owner);
        await schema.LoadAsync(Ct);
        // A manual relationship gets no badge, as in the table grid (only declared ones and those of the C# model do).
        schema.SetForeignKeys(FkSource.Manual, [new("MANUELL", Auftrag.Table.Ref, ["AUFTRAG_ID"], Kunden.Table.Ref, ["KUNDE_ID"], FkSource.Manual)]);

        var kunde = new EntityExport("Shop.Kunde", "Shop.Kunde", false, null, "KUNDEN", null, null, null,
            [
                new("KundeId", "int", "System.Int32", false, false, "KUNDE_ID", null, null, null, false, null),
                new("Name", "string", "System.String", false, false, "NAME", null, null, null, false, null),
                new("Kundenart", "Kundenart", "Shop.Kundenart", false, false, "KUNDENART", null, null, "int", false,
                    [new("Privat", "1", "1"), new("Gewerbe", "2", "2")]),
            ],
            ["KundeId"], [], "Kunden");
        var mapping = await ClrModelMapping.BuildAsync(new ModelExport(1, "8.0.0", "Shop.Ctx", "options", null, [kunde]), schema, Ct);

        var sources = await SqlResultSources.LoadAsync(Sql, Columns, schema, Ct);
        return new SqlResultPresentation(Columns, sources, d => TablePresentation.Create(d, mapping, names), schema.OutgoingOf);
    }

    [Fact]
    public async Task Traced_columns_carry_the_names_badges_and_origin_of_their_table()
    {
        var columns = (await PresentationAsync(ClrNameDisplay.Beside)).Columns();

        Assert.True(columns[1].Fk);
        Assert.Equal((false, false), (columns[0].Fk, columns[0].FkModel));
        Assert.Equal("Aus AUFTRAG.KUNDE_ID", columns[1].Comment);
        Assert.Null(columns[1].Alternate); // AUFTRAG has no entity
        Assert.All(columns, c => Assert.False(c.Pk)); // grid.js would pin key columns to the left

        Assert.Equal(("ART", "Kundenart", "Kundenart"), (columns[2].Label, columns[2].Alternate, columns[2].ClrType));
        Assert.False(columns[2].Numeric); // shown as enum members
        Assert.Equal("Aus KUNDEN.KUNDENART\n1 privat, 2 Gewerbe", columns[2].Comment);
        Assert.All(columns, c => Assert.True(c.ClrLine));

        Assert.Equal(("ONE", null, true, null), (columns[3].Label, columns[3].Alternate, columns[3].Numeric, columns[3].Comment));
    }

    [Fact]
    public async Task With_CSharp_names_in_front_the_property_leads_and_the_result_name_follows()
    {
        var presentation = await PresentationAsync(ClrNameDisplay.Front);

        var art = presentation.Columns()[2];

        Assert.Equal(("Kundenart", "ART"), (art.Label, art.Alternate));
        Assert.Equal(GridOptions.ClrHeaderHeight, presentation.HeaderHeight);
    }

    [Fact]
    public async Task Enum_values_read_by_their_member_and_unknown_ones_are_marked()
    {
        var presentation = await PresentationAsync(ClrNameDisplay.Beside);

        var known = presentation.Row([10m, 4711m, 2m, 1m]);
        var unknown = presentation.Row([10m, 4711m, 9m, 1m]);

        Assert.Equal("Gewerbe (2)", known["c2"]);
        Assert.False(known.ContainsKey("__u"));
        Assert.Equal(["c2"], (IEnumerable<string>)unknown["__u"]!);
        Assert.Equal("4.711", known["c1"]);
    }

    [Fact]
    public async Task The_excel_export_takes_property_names_and_members_of_traced_columns()
    {
        var beside = (await PresentationAsync(ClrNameDisplay.Beside)).ExportColumns();
        var off = (await PresentationAsync(ClrNameDisplay.Off)).ExportColumns();

        // AUFTRAG has no entity: its columns keep the result's names.
        Assert.Equal(["AUFTRAG_ID", "KUNDE_ID", "Kundenart", "ONE"], beside.Select(c => c.Header));
        Assert.Equal([0, 1, 2, 3], beside.Select(c => c.Source));
        Assert.Equal("Gewerbe (2)", beside[2].Present!(2m));
        Assert.Equal(9m, beside[2].Present!(9m)); // no member: the number stays a number
        Assert.Equal(["AUFTRAG_ID", "KUNDE_ID", "ART", "ONE"], off.Select(c => c.Header));
        Assert.True(ExportColumn.DiffersFromRaw(off)); // members by name
        Assert.False(ExportColumn.DiffersFromRaw(SqlResultPresentation.Plain(Columns).ExportColumns()));
    }

    [Fact]
    public void A_plain_result_is_shown_as_the_driver_reports_it()
    {
        var presentation = SqlResultPresentation.Plain(Columns);

        Assert.Null(presentation.HeaderHeight);
        Assert.All(presentation.Columns(), c => Assert.Equal((false, false, null), (c.Fk, c.ClrLine, c.Comment)));
        Assert.Equal("2", presentation.Row([10m, 4711m, 2m, 1m])["c2"]);
    }
}
