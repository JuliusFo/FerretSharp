using FerretSharp.Core.Audit;
using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Schema;
using NSubstitute;

namespace FerretSharp.Core.Tests.ClrModel.Report;

/// <summary>WP-72: the report of how the C# model and the database fit, from what is loaded – no query of its own.</summary>
public sealed class ModelReportTests
{
    private const string Owner = "SHOP";

    private static readonly TableRef Auftrag = new(Owner, "AUFTRAG");
    private static readonly TableRef Kunde = new(Owner, "KUNDE");
    private static readonly TableRef Lager = new(Owner, "LAGER");
    private static readonly TableRef Gutschein = new(Owner, "GUTSCHEIN");

    private readonly ISchemaReader _reader = Substitute.For<ISchemaReader>();
    private readonly Dictionary<string, IReadOnlyList<ColumnInfo>> _columns = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ColumnInfo Column(string name, int position, bool nullable = false, string? @default = null, bool identity = false, bool isVirtual = false) =>
        new(name, "NUMBER", null, false, 10, 0, nullable, identity, @default, position, IsVirtual: isVirtual);

    private static PropertyExport Prop(string name, string column) => new(name, "int", false, false, column, null, null, null, false, null);

    private static EntityExport Entity(string name, string table, PropertyExport[] properties, ForeignKeyExport[]? foreignKeys = null) =>
        new("Shop." + name, "Shop." + name, false, null, table, null, null, null, properties, [properties[0].Name], foreignKeys ?? []);

    private static ForeignKeyInfo Declared(string name, TableRef from, string column, TableRef to, string toColumn) =>
        new(name, from, [column], to, [toColumn], FkSource.Declared);

    private async Task<SchemaCache> SchemaAsync(TableSummary[] tables, ForeignKeyInfo[] foreignKeys, PrimaryKeyInfo[] keys, AuditTemplate? audit = null)
    {
        _reader.GetTablesAsync(Owner, Arg.Any<CancellationToken>()).Returns(tables);
        _reader.GetSynonymTargetsAsync(Owner, Arg.Any<CancellationToken>()).Returns(SynonymTargets.None);
        _reader.GetForeignKeysAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).Returns(foreignKeys);
        _reader.GetPrimaryKeysAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).Returns(keys);
        _reader.GetColumnsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ => (IReadOnlyDictionary<string, IReadOnlyList<ColumnInfo>>)_columns);
        var schema = new SchemaCache(_reader, Owner);
        schema.UseAuditTemplate(() => audit);
        await schema.LoadAsync(Ct);
        return schema;
    }

    /// <summary>
    /// AUFTRAG, KUNDE, LAGER mapped; GUTSCHEIN, IMPORT_TMP, AUDIT_AUFTRAG (audit table) and a view without entity. Declared:
    /// AUFTRAG → KUNDE (also in the model), AUFTRAG → LAGER (not in the model), AUFTRAG → GUTSCHEIN (table without entity),
    /// GUTSCHEIN → KUNDE (from a table without entity). Only in the model: KUNDE → LAGER (PK on LAGER) and LAGER → KUNDE
    /// over a column that is not KUNDE's key.
    /// </summary>
    private async Task<(ClrModelMapping Mapping, SchemaCache Schema)> ShopAsync(AuditTemplate? audit = null)
    {
        _columns["AUFTRAG"] = [Column("AUFTRAG_ID", 1), Column("KUNDE_ID", 2), Column("LAGER_ID", 3, nullable: true), Column("GUTSCHEIN_ID", 4, nullable: true),
            Column("RABATT", 5, nullable: true), Column("STORNO_GRUND", 6), Column("ANGELEGT", 7, @default: "SYSDATE"), Column("SUMME", 8, isVirtual: true)];
        _columns["KUNDE"] = [Column("KUNDE_ID", 1), Column("LAGER_ID", 2, nullable: true), Column("NUMMER", 3)];
        _columns["LAGER"] = [Column("LAGER_ID", 1), Column("KUNDE_NR", 2, nullable: true)];
        var tables = new TableSummary[]
        {
            new(Owner, "AUFTRAG", TableKind.Table), new(Owner, "KUNDE", TableKind.Table), new(Owner, "LAGER", TableKind.Table),
            new(Owner, "GUTSCHEIN", TableKind.Table), new(Owner, "IMPORT_TMP", TableKind.Table), new(Owner, "AUDIT_AUFTRAG", TableKind.Table),
            new(Owner, "V_UMSATZ", TableKind.View),
        };
        var foreignKeys = new[]
        {
            Declared("FK_AUFTRAG_KUNDE", Auftrag, "KUNDE_ID", Kunde, "KUNDE_ID"),
            Declared("FK_AUFTRAG_LAGER", Auftrag, "LAGER_ID", Lager, "LAGER_ID"),
            Declared("FK_AUFTRAG_GUTSCHEIN", Auftrag, "GUTSCHEIN_ID", Gutschein, "GUTSCHEIN_ID"),
            Declared("FK_GUTSCHEIN_KUNDE", Gutschein, "KUNDE_ID", Kunde, "KUNDE_ID"),
        };
        var keys = new[] { new PrimaryKeyInfo(Auftrag, ["AUFTRAG_ID"]), new PrimaryKeyInfo(Kunde, ["KUNDE_ID"]), new PrimaryKeyInfo(Lager, ["LAGER_ID"]) };
        var schema = await SchemaAsync(tables, foreignKeys, keys, audit);
        var model = new ModelExport(4, "8.0.11", "Shop.ShopContext", "options", null,
        [
            Entity("Auftrag", "AUFTRAG", [Prop("AuftragId", "AUFTRAG_ID"), Prop("KundeId", "KUNDE_ID"), Prop("LagerId", "LAGER_ID")],
                [new ForeignKeyExport(["KundeId"], "Shop.Kunde", ["KundeId"], "Kunde", null, false)]),
            Entity("Kunde", "KUNDE", [Prop("KundeId", "KUNDE_ID"), Prop("LagerId", "LAGER_ID"), Prop("Nummer", "NUMMER")],
                [new ForeignKeyExport(["LagerId"], "Shop.Lager", ["LagerId"], "Lager", null, false)]),
            Entity("Lager", "LAGER", [Prop("LagerId", "LAGER_ID"), Prop("KundeNr", "KUNDE_NR")],
                [new ForeignKeyExport(["KundeNr"], "Shop.Kunde", ["Nummer"], "Kunde", null, false)]),
            Entity("Newsletter", "NEWSLETTER", [Prop("Id", "ID")]),
        ]);
        return (await ClrModelMapping.BuildAsync(model, schema, Ct), schema);
    }

    [Fact]
    public async Task Tables_without_entity_leave_out_audit_tables_and_exceptions_but_keep_them_countable()
    {
        var (mapping, schema) = await ShopAsync(AuditTemplates.Default);
        var exceptions = ReportExceptions.From(new Dictionary<string, string?> { ["import_*"] = "Loaded nightly" });

        var report = ModelReport.Build(mapping, schema, exceptions);

        Assert.Equal(
            [("AUDIT_AUFTRAG", true), ("GUTSCHEIN", false), ("IMPORT_TMP", true), ("V_UMSATZ", false)],
            report.Tables.Select(t => (t.Table.Name, t.IsHidden)));
        Assert.Equal(Auftrag, report.Tables[0].AuditOf);
        Assert.Equal("import_*", report.Tables[2].Exception!.Table);
        Assert.Equal(["GUTSCHEIN"], report.MissingTables.Select(t => t.Table.Name));
        // The view is listed but does not count; hidden tables do not count either.
        Assert.Equal((4, 3), (report.TablesInScope, report.TablesMapped));
        var gutschein = report.Tables[1];
        Assert.Equal(["FK_GUTSCHEIN_KUNDE"], gutschein.Outgoing.Select(f => f.Name));
        Assert.Equal(["FK_AUFTRAG_GUTSCHEIN"], gutschein.Incoming.Select(f => f.Name));
    }

    [Fact]
    public async Task Columns_without_property_carry_their_type_and_mark_an_insert_that_would_fail()
    {
        var (mapping, schema) = await ShopAsync();

        var report = ModelReport.Build(mapping, schema, ReportExceptions.Empty);

        Assert.Equal(
            [
                ("AUFTRAG", "GUTSCHEIN_ID", false), ("AUFTRAG", "RABATT", false), ("AUFTRAG", "STORNO_GRUND", true),
                ("AUFTRAG", "ANGELEGT", false), ("AUFTRAG", "SUMME", false),
            ],
            report.Columns.Select(c => (c.Entity.Table.Name, c.Column.Name, c.RequiredWithoutDefault)));
        Assert.Equal("NUMBER(10)", report.Columns[0].Column.DisplayType);
    }

    [Fact]
    public async Task Relationships_are_compared_over_tables_and_column_pairs()
    {
        var (mapping, schema) = await ShopAsync();

        var report = ModelReport.Build(mapping, schema, ReportExceptions.Empty);

        // KUNDE → LAGER references LAGER's key: a constraint can be created. LAGER → KUNDE references NUMMER, not the key.
        Assert.Equal(
            [("Kunde.Lager", true), ("Lager.Kunde", false)],
            report.ModelOnly.Select(r => (r.Relation.Name, r.ConstraintPossible)));
        // AUFTRAG → KUNDE is in both; GUTSCHEIN → KUNDE belongs to the table without entity.
        Assert.Equal(
            [("FK_AUFTRAG_LAGER", DbOnlyReason.NoNavigation, true), ("FK_AUFTRAG_GUTSCHEIN", DbOnlyReason.TableWithoutEntity, false)],
            report.DbOnly.Select(r => (r.ForeignKey.Name, r.Reason, r.HasProperties)));
    }

    [Fact]
    public async Task A_foreign_key_to_a_table_meant_to_have_no_entity_is_expected()
    {
        var (mapping, schema) = await ShopAsync();

        var report = ModelReport.Build(mapping, schema, ReportExceptions.From(new Dictionary<string, string?> { ["GUTSCHEIN"] = null }));

        Assert.Equal(["FK_AUFTRAG_LAGER"], report.DbOnly.Select(r => r.ForeignKey.Name));
        Assert.True(report.Tables.Single(t => t.Table.Ref == Gutschein).IsHidden);
    }

    [Fact]
    public async Task The_model_without_counterpart_and_the_mismatches_come_from_the_mapping()
    {
        var (mapping, schema) = await ShopAsync();

        var report = ModelReport.Build(mapping, schema, ReportExceptions.Empty);

        Assert.Equal([(MappingIssueKind.EntityWithoutTable, "Shop.Newsletter")], report.ModelWithoutMatch.Select(i => (i.Kind, i.Entity)));
        Assert.Same(mapping.ColumnMismatches, report.Mismatches);
        Assert.Equal(["SHOP"], report.Owners);
    }

    [Fact]
    public async Task Navigation_code_for_a_foreign_key_the_model_lacks()
    {
        var (mapping, schema) = await ShopAsync();
        var report = ModelReport.Build(mapping, schema, ReportExceptions.Empty);

        var code = EntityCode.Navigation(mapping, report.DbOnly[0].ForeignKey, schema.OutgoingOf(Auftrag));

        Assert.Equal(
            """
            // Auftrag
            public Lager? Lager { get; set; }

            // AuftragConfiguration
            builder.HasOne(x => x.Lager).WithMany().HasForeignKey(x => x.LagerId);
            """.ReplaceLineEndings("\n"),
            code);
        Assert.Null(EntityCode.Navigation(mapping, report.DbOnly[1].ForeignKey, []));
    }

    [Fact]
    public async Task Navigation_code_names_a_missing_foreign_key_property_in_a_comment()
    {
        var (mapping, _) = await ShopAsync();
        var fk = Declared("FK_X", Auftrag, "GUTSCHEIN_ID", Kunde, "KUNDE_ID");

        var code = EntityCode.Navigation(mapping, fk, [])!;

        // The model has a navigation from AUFTRAG to KUNDE already: the new one is named after its column.
        Assert.Contains("public Kunde? Gutschein { get; set; }", code, StringComparison.Ordinal);
        Assert.Contains(".HasForeignKey(x => x.GutscheinId);", code, StringComparison.Ordinal);
        Assert.EndsWith("// GUTSCHEIN_ID hat noch keine Property: zuerst anlegen (Bericht: Spalten ohne Property).", code, StringComparison.Ordinal);
    }

    /// <summary>Review of WP-72: a member named like its class does not compile (CS0542), two navigations of one name neither.</summary>
    [Fact]
    public async Task Navigation_code_avoids_the_class_name_and_a_second_foreign_key_to_the_same_entity()
    {
        var (mapping, _) = await ShopAsync();
        var toItself = Declared("FK_KUNDE_KUNDE", Kunde, "LAGER_ID", Kunde, "KUNDE_ID");
        var delivery = Declared("FK_AUFTRAG_LAGER", Auftrag, "LAGER_ID", Lager, "LAGER_ID");
        var other = Declared("FK_AUFTRAG_LAGER2", Auftrag, "GUTSCHEIN_ID", Lager, "LAGER_ID");

        var self = EntityCode.Navigation(mapping, toItself, [toItself])!;
        var alone = EntityCode.Navigation(mapping, other, [other])!;
        var second = EntityCode.Navigation(mapping, other, [delivery, other])!;

        Assert.DoesNotContain("public Kunde? Kunde ", self, StringComparison.Ordinal);
        Assert.Contains("public Kunde? LagerNavigation { get; set; }", self, StringComparison.Ordinal); // Kunde.Lager exists
        Assert.Contains("public Lager? Lager { get; set; }", alone, StringComparison.Ordinal);
        Assert.Contains("public Lager? Gutschein { get; set; }", second, StringComparison.Ordinal);
    }

    /// <summary>Review of WP-72: the copied property must not clash with a member or the class, and keeps the notes.</summary>
    [Fact]
    public async Task Property_code_gets_a_free_name_and_the_notes_as_comments()
    {
        var (mapping, _) = await ShopAsync();
        var auftrag = mapping.EntityOf(Auftrag)!;

        var clash = EntityCode.Property(mapping, auftrag, new ColumnInfo("AUFTRAG", "NUMBER", null, false, 10, 0, true, false, null, 9));
        // NUMMER is Kunde.Nummer elsewhere: the suggestion takes that name and says so in a note.
        var elsewhere = EntityCode.Property(mapping, auftrag, new ColumnInfo("NUMMER", "NUMBER", null, false, 10, 0, false, false, null, 9));

        Assert.Contains("public long? Auftrag2 { get; set; }", clash, StringComparison.Ordinal);
        // The convention would map Auftrag2 to AUFTRAG2: the renamed property names its column (second review pass).
        Assert.Contains("builder.Property(x => x.Auftrag2).HasColumnName(\"AUFTRAG\")", clash, StringComparison.Ordinal);
        Assert.StartsWith("// Auftrag\npublic int Nummer { get; set; }\n\n// AuftragConfiguration\n", elsewhere, StringComparison.Ordinal);
        Assert.Matches(";\n// .*NUMMER", elsewhere); // the notes of the suggestion as comments
    }
}
