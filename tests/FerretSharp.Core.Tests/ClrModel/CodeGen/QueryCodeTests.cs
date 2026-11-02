using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Query;
using FerretSharp.Core.Relations;
using FerretSharp.Core.Schema;
using FerretSharp.Testing;
using NSubstitute;

namespace FerretSharp.Core.Tests.ClrModel;

public class QueryCodeTests
{
    private const string Owner = "APP";

    private static ColumnInfo Col(string name, string type = "NUMBER", bool nullable = true) =>
        new(name, type, type == "VARCHAR2" ? 50 : null, false, type == "NUMBER" ? 10 : null, type == "NUMBER" ? 0 : null, nullable, false, null, 0);

    private static TableDetails Table(string name, string[] key, params ColumnInfo[] columns) =>
        new(new TableSummary(Owner, name, TableKind.Table), columns, key, [], false);

    private static readonly TableDetails Auftrag = Table("AUFTRAG", ["ID"],
        Col("ID", nullable: false), Col("NR", nullable: false), Col("DATUM", "DATE"), Col("KUNDE_ID", nullable: false), Col("BEARBEITER_ID"),
        Col("GEAENDERT", "TIMESTAMP(6) WITH TIME ZONE"), Col("INTERN"));
    private static readonly TableDetails Kunde = Table("KUNDE", ["ID"], Col("ID", nullable: false), Col("NAME", "VARCHAR2", nullable: false), Col("UMSATZ"));
    private static readonly TableDetails Mitarbeiter = Table("MITARBEITER", ["ID"], Col("ID", nullable: false), Col("NR", nullable: false), Col("NAME", "VARCHAR2"));
    private static readonly TableDetails Position = Table("POSITION", ["AUFTRAG_ID", "POS_NR"],
        Col("AUFTRAG_ID", nullable: false), Col("POS_NR", nullable: false), Col("MENGE"), Col("ARTIKEL_ID"));
    private static readonly TableDetails Artikel = Table("ARTIKEL", ["ID"], Col("ID", nullable: false), Col("NAME", "VARCHAR2", nullable: false));
    private static readonly TableDetails Notiz = Table("NOTIZ", ["ID"], Col("ID", nullable: false), Col("AUFTRAG_ID"));

    private static readonly Dictionary<TableRef, TableDetails> Tables =
        new[] { Auftrag, Kunde, Mitarbeiter, Position, Artikel, Notiz }.ToDictionary(t => t.Table.Ref);

    private static ForeignKeyInfo Fk(string name, TableDetails from, string column, TableDetails to) =>
        new(name, from.Table.Ref, [column], to.Table.Ref, ["ID"], FkSource.Declared) { Enforced = true };

    private static readonly ForeignKeyInfo AuftragKunde = Fk("FK_AUFTRAG_KUNDE", Auftrag, "KUNDE_ID", Kunde);
    private static readonly ForeignKeyInfo AuftragBearbeiter = Fk("FK_AUFTRAG_BEARBEITER", Auftrag, "BEARBEITER_ID", Mitarbeiter);
    private static readonly ForeignKeyInfo PositionAuftrag = Fk("FK_POSITION_AUFTRAG", Position, "AUFTRAG_ID", Auftrag);
    private static readonly ForeignKeyInfo PositionArtikel = Fk("FK_POSITION_ARTIKEL", Position, "ARTIKEL_ID", Artikel);
    private static readonly ForeignKeyInfo NotizAuftrag = Fk("FK_NOTIZ_AUFTRAG", Notiz, "AUFTRAG_ID", Auftrag);

    private static readonly RelationGraph Graph = new([AuftragKunde, AuftragBearbeiter, PositionAuftrag, PositionArtikel, NotizAuftrag]);

    private static PropertyExport Property(string name, string type, string column, bool nullable = false) =>
        new(name, type, nullable, false, column, null, null, null, false, null);

    private static ForeignKeyExport Key(string property, string principal, string? navigation, string? inverse) =>
        new([property], principal, ["Id"], navigation, inverse, false);

    /// <summary>The model: every table but NOTIZ has an entity; POSITION → ARTIKEL has no inverse navigation.</summary>
    private static async Task<ClrModelMapping> MappingAsync()
    {
        var reader = Substitute.For<ISchemaReader>();
        reader.GetTablesAsync(Owner, Arg.Any<CancellationToken>()).Returns((IReadOnlyList<TableSummary>)[.. Tables.Values.Select(t => t.Table)]);
        reader.GetSynonymTargetsAsync(Owner, Arg.Any<CancellationToken>()).Returns(SynonymTargets.None);
        reader.GetForeignKeysAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).Returns((IReadOnlyList<ForeignKeyInfo>)[]);
        reader.GetColumnsAsync(Owner, Arg.Any<CancellationToken>()).Returns((IReadOnlyDictionary<string, IReadOnlyList<ColumnInfo>>)
            Tables.Values.ToDictionary(t => t.Table.Name, t => t.Columns));
        var schema = new SchemaCache(reader, Owner);
        await schema.LoadAsync(TestContext.Current.CancellationToken);

        EntityExport Entity(string name, string table, string? dbSet, PropertyExport[] properties, string[] key, ForeignKeyExport[] keys) =>
            new($"Shop.{name}", $"Shop.{name}", false, null, table, null, null, null, properties, key, keys, dbSet);

        var entities = new[]
        {
            Entity("Auftrag", "AUFTRAG", "Auftraege",
                [Property("Id", "int", "ID"), Property("Nr", "int", "NR"), Property("Datum", "DateTime", "DATUM", nullable: true),
                 Property("KundeId", "int", "KUNDE_ID"), Property("BearbeiterId", "int", "BEARBEITER_ID", nullable: true),
                 Property("Geaendert", "DateTimeOffset", "GEAENDERT", nullable: true)],
                ["Id"], [Key("KundeId", "Shop.Kunde", "Kunde", "Auftraege"), Key("BearbeiterId", "Shop.Mitarbeiter", "Bearbeiter", null)]),
            Entity("Kunde", "KUNDE", "Kunden", [Property("Id", "int", "ID"), Property("Name", "string", "NAME"), Property("Umsatz", "decimal", "UMSATZ", nullable: true)], ["Id"], []),
            Entity("Mitarbeiter", "MITARBEITER", null, [Property("Id", "int", "ID"), Property("Nr", "int", "NR"), Property("Name", "string", "NAME", nullable: true)], ["Id"], []),
            Entity("AuftragPosition", "POSITION", null,
                [Property("AuftragId", "int", "AUFTRAG_ID"), Property("PosNr", "int", "POS_NR"), Property("Menge", "decimal", "MENGE", nullable: true),
                 Property("ArtikelId", "int", "ARTIKEL_ID", nullable: true)],
                ["AuftragId", "PosNr"], [Key("AuftragId", "Shop.Auftrag", "Auftrag", "Positionen"), Key("ArtikelId", "Shop.Artikel", "Artikel", null)]),
            Entity("Artikel", "ARTIKEL", null, [Property("Id", "int", "ID"), Property("Name", "string", "NAME")], ["Id"], []),
        };
        return await ClrModelMapping.BuildAsync(new ModelExport(1, "8.0.0", "Shop.Ctx", "options", null, entities), schema, TestContext.Current.CancellationToken);
    }

    private static QueryStep Up(ForeignKeyInfo relation) => new(RelationGraph.KeyOf(relation), false);

    private static QueryStep Down(ForeignKeyInfo relation) => new(RelationGraph.KeyOf(relation), true);

    private static QueryNodeState Columns(QueryNodeState node, params string[] columns) => node with { Columns = [.. columns.Select(c => new QueryColumn(c))] };

    private static async Task<QueryCode> BuildAsync(QueryDesignState state, QueryCodeOptions? options = null) =>
        QueryCodeBuilder.Build(QueryDesign.Resolve(Auftrag.Table.Ref, state, Graph), await MappingAsync(), t => Tables[t],
            options ?? QueryCodeOptions.For("Auftrag"));

    private static readonly QueryStep[] PositionPath = [Down(PositionAuftrag)];
    private static readonly QueryStep[] ArtikelPath = [Down(PositionAuftrag), Up(PositionArtikel)];

    /// <summary>The root, a required and an optional parent, a chosen child with a filter, an order and its parent.</summary>
    private static QueryDesignState Sample() => QueryDesignState.Empty
        .Update([], n => Columns(n, "NR") with { Filters = [new QueryFilter(FilterCondition.Of("NR", FilterOperator.Gte, "1000"))] })
        .Add([], Up(AuftragKunde)).Update([Up(AuftragKunde)], n => Columns(n, "NAME"))
        .Add([], Up(AuftragBearbeiter)).Update([Up(AuftragBearbeiter)], n => Columns(n, "NR") with
        {
            Filters = [new QueryFilter(FilterCondition.Of("NAME", FilterOperator.StartsWith, "M"))],
        })
        .Add([], Down(PositionAuftrag)).Update(PositionPath, n => Columns(n, "MENGE") with
        {
            Filters = [new QueryFilter(FilterCondition.Of("MENGE", FilterOperator.Gt, "0"))],
            Sorts = [new SortSpec("MENGE", true)],
        })
        .Add(PositionPath, Up(PositionArtikel)).Update(ArtikelPath, n => Columns(n, "NAME"));

    [Fact]
    public async Task Writes_an_expression_per_value_a_helper_for_the_chosen_child_a_predicate_a_projection_and_the_dto()
    {
        var code = await BuildAsync(Sample());

        Assert.Equal(
            """
            using System.Linq.Expressions;
            using Microsoft.EntityFrameworkCore;
            using LinqKit;

            public static class AuftragQueries
            {
                private static readonly Expression<Func<Auftrag, AuftragPosition?>> FirstAuftragPosition = a =>
                    a.Positionen.Where(p => p.Menge > 0m).OrderByDescending(p => p.Menge).ThenBy(p => p.AuftragId).ThenBy(p => p.PosNr).FirstOrDefault();

                internal static readonly Expression<Func<Auftrag, string>> KundeName = a => a.Kunde.Name;

                internal static readonly Expression<Func<Auftrag, int?>> BearbeiterNr = a => (int?)a.Bearbeiter!.Nr;

                internal static readonly Expression<Func<Auftrag, decimal?>> FirstAuftragPositionMenge =
                    Linq.Expr((Auftrag a) => FirstAuftragPosition.Invoke(a)!.Menge).Expand();

                internal static readonly Expression<Func<Auftrag, string?>> FirstAuftragPositionArtikelName =
                    Linq.Expr((Auftrag a) => FirstAuftragPosition.Invoke(a)!.Artikel!.Name).Expand();

                public static readonly Expression<Func<Auftrag, bool>> Filter = a =>
                    a.Nr >= 1000
                    && a.Bearbeiter!.Name.ToUpper().StartsWith("M");

                public static readonly Expression<Func<Auftrag, AuftragRow>> Projection =
                    Linq.Expr((Auftrag a) => new AuftragRow
                    {
                        Nr = a.Nr,
                        KundeName = KundeName.Invoke(a),
                        BearbeiterNr = BearbeiterNr.Invoke(a),
                        FirstAuftragPositionMenge = FirstAuftragPositionMenge.Invoke(a),
                        FirstAuftragPositionArtikelName = FirstAuftragPositionArtikelName.Invoke(a),
                    }).Expand();
            }

            public sealed record AuftragRow
            {
                public int Nr { get; init; }

                public string KundeName { get; init; } = "";

                public int? BearbeiterNr { get; init; }

                public decimal? FirstAuftragPositionMenge { get; init; }

                public string? FirstAuftragPositionArtikelName { get; init; }
            }
            """.ReplaceLineEndings("\r\n"), code.Code);
        Assert.Equal("db.Auftraege\r\n    .Where(AuftragQueries.Filter)\r\n    .Select(AuftragQueries.Projection)", code.Usage);
        Assert.Empty(code.Notes);
    }

    [Fact]
    public async Task A_chosen_child_with_several_values_offers_the_query_that_reads_it_once()
    {
        var code = await BuildAsync(Sample());

        Assert.Equal(
            """
            db.Auftraege
                .Where(AuftragQueries.Filter)
                .SelectMany(a => a.Positionen.Where(p => p.Menge > 0m).OrderByDescending(p => p.Menge).ThenBy(p => p.AuftragId).ThenBy(p => p.PosNr).Take(1).DefaultIfEmpty(), (a, p) => new { a, p })
                .Select(x => new AuftragRow
                {
                    Nr = x.a.Nr,
                    KundeName = x.a.Kunde.Name,
                    BearbeiterNr = (int?)x.a.Bearbeiter!.Nr,
                    FirstAuftragPositionMenge = x.p!.Menge,
                    FirstAuftragPositionArtikelName = x.p!.Artikel!.Name,
                })
            """.ReplaceLineEndings("\r\n"), code.SingleRead);
    }

    [Fact]
    public async Task Conditions_as_parameters_make_the_predicate_a_method_and_the_console_declares_them()
    {
        var state = QueryDesignState.Empty.Update([], n => Columns(n, "NR") with
        {
            Filters =
            [
                new QueryFilter(FilterCondition.Of("DATUM", FilterOperator.Equals, "1.10.2026"), "tag"),
                new QueryFilter(FilterCondition.Of("NR", FilterOperator.Between, "1", "9"), "nr"),
            ],
        });

        var code = await BuildAsync(state);

        Assert.Contains(
            "public static Expression<Func<Auftrag, bool>> Filter(DateTime tag, int nrFrom, int nrTo) => a =>\r\n"
            + "        a.Datum >= tag.Date && a.Datum < tag.Date.AddDays(1)\r\n        && a.Nr >= nrFrom && a.Nr <= nrTo;", code.Code, StringComparison.Ordinal);
        Assert.Equal("DateTime tag = new DateTime(2026, 10, 1);\r\nint nrFrom = 1;\r\nint nrTo = 9;", code.Variables);
        Assert.Contains(".Where(AuftragQueries.Filter(tag, nrFrom, nrTo))", code.Usage, StringComparison.Ordinal);
        Assert.Null(code.SingleRead);
    }

    [Fact]
    public async Task Parameters_get_names_unique_in_the_code_and_a_list_takes_the_member_s_nullable_type()
    {
        // Review: a range on one column gave Filter(DateTime datum, DateTime datum) (CS0100), a parameter named as the
        // lambda's CS0136, and a list for an int? was an int[] without Contains for it (CS1929).
        var state = QueryDesignState.Empty.Update([], n => Columns(n, "NR") with
        {
            Filters =
            [
                new QueryFilter(FilterCondition.Of("DATUM", FilterOperator.Gte, "1.10.2026 08:00"), "datum"),
                new QueryFilter(FilterCondition.Of("DATUM", FilterOperator.Lt, "1.11.2026 08:00"), "datum"),
                new QueryFilter(FilterCondition.Of("NR", FilterOperator.Equals, "5"), "a"),
                new QueryFilter(FilterCondition.Of("BEARBEITER_ID", FilterOperator.In, "1", "2"), "Nr"),
            ],
        });

        var code = await BuildAsync(state);

        Assert.Contains(
            "Filter(DateTime datum, DateTime datum2, int a2, int?[] Nr2) => a =>\r\n"
            + "        a.Datum >= datum\r\n        && a.Datum < datum2\r\n        && a.Nr == a2\r\n        && Nr2.Contains(a.BearbeiterId);",
            code.Code, StringComparison.Ordinal);
        Assert.Equal(
            "DateTime datum = new DateTime(2026, 10, 1, 8, 0, 0);\r\nDateTime datum2 = new DateTime(2026, 11, 1, 8, 0, 0);\r\nint a2 = 5;\r\nint?[] Nr2 = new int?[] { 1, 2 };",
            code.Variables);
        Assert.Contains(".Where(AuftragQueries.Filter(datum, datum2, a2, Nr2))", code.Usage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_condition_left_there_is_no_filter_to_call_and_a_whole_day_on_a_date_time_offset_is_no_parameter()
    {
        // Review: the usage called AuftragQueries.Filter although every condition was left out (CS0117), and a day on a
        // DateTimeOffset compared the instant in C# but the whole day in SQL.
        var state = QueryDesignState.Empty.Update([], n => Columns(n, "NR") with
        {
            Filters =
            [
                new QueryFilter(FilterCondition.Of("INTERN", FilterOperator.Equals, "1")),
                new QueryFilter(FilterCondition.Of("GEAENDERT", FilterOperator.Equals, "1.10.2026"), "tag"),
            ],
        });

        using (UiCulture.Use("en"))
        {
            var code = await BuildAsync(state);

            Assert.DoesNotContain("Filter", code.Code, StringComparison.Ordinal);
            Assert.Equal("db.Auftraege\r\n    .Select(AuftragQueries.Projection)", code.Usage);
            Assert.Contains(code.Notes, n => n.Contains("A date without time cannot be a parameter here", StringComparison.Ordinal));
            Assert.Equal(2, code.Notes.Count);
        }
    }

    [Fact]
    public async Task Keywords_names_of_the_class_or_dto_and_invalid_parameter_or_namespace_names_are_caught()
    {
        var state = QueryDesignState.Empty.Update([], n => n with
        {
            Columns = [new QueryColumn("NR", "AuftragRow"), new QueryColumn("DATUM", "class")],
            Filters = [new QueryFilter(FilterCondition.Of("NR", FilterOperator.Equals, "5"), "nicht gültig")],
        });

        using (UiCulture.Use("en"))
        {
            var code = await BuildAsync(state, QueryCodeOptions.For("Auftrag") with { Namespace = "Shop.1x" });

            Assert.Contains("    AuftragRow2 = a.Nr,", code.Code, StringComparison.Ordinal); // CS0542 as a member of AuftragRow
            Assert.Contains("    Datum = a.Datum,", code.Code, StringComparison.Ordinal);
            Assert.Contains("Filter = a =>\r\n        a.Nr == 5;", code.Code, StringComparison.Ordinal);
            Assert.DoesNotContain("namespace", code.Code, StringComparison.Ordinal);
            Assert.Equal(
            [
                "“class” is not a C# name – Datum is used instead.",
                "AUFTRAG: “nicht gültig” is not a C# name – the condition takes its value instead of a parameter.",
                "Namespace “Shop.1x” is not a C# namespace – left out.",
            ], code.Notes);
        }
    }

    [Fact]
    public async Task Without_linqkit_every_value_stands_in_the_projection_as_a_whole_path()
    {
        var code = await BuildAsync(Sample(), QueryCodeOptions.For("Auftrag") with { LinqKit = false, WithDto = false });

        Assert.DoesNotContain("Invoke", code.Code, StringComparison.Ordinal);
        Assert.DoesNotContain("record AuftragRow", code.Code, StringComparison.Ordinal);
        Assert.Contains(
            "FirstAuftragPositionArtikelName = a.Positionen.Where(p => p.Menge > 0m).OrderByDescending(p => p.Menge).ThenBy(p => p.AuftragId).ThenBy(p => p.PosNr).FirstOrDefault()!.Artikel!.Name,",
            code.Code, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_template_replaces_the_projection_with_the_project_s_own_mapping_but_the_console_keeps_it()
    {
        var template = new QueryLineTemplate(
            "public static readonly Mapping<{Entity}, {Dto}> All = new MappingBuilder<{Entity}, {Dto}>()", ".Bind({Expression}, d => d.{Property})", "    .Build();");

        var code = await BuildAsync(Sample(), QueryCodeOptions.For("Auftrag") with { Template = template });

        Assert.Contains(
            """
                public static readonly Mapping<Auftrag, AuftragRow> All = new MappingBuilder<Auftrag, AuftragRow>()
                    .Bind(a => a.Nr, d => d.Nr)
                    .Bind(KundeName, d => d.KundeName)
            """.ReplaceLineEndings("\r\n"), code.Code, StringComparison.Ordinal);
        Assert.DoesNotContain("Projection", code.Code, StringComparison.Ordinal);
        Assert.Contains("Projection =", code.Script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task What_has_no_c_sharp_form_stays_out_with_a_note_and_names_are_checked()
    {
        var state = QueryDesignState.Empty.Update([], n => n with { Columns = [new QueryColumn("NR", "nicht gültig!")] })
            .Add([], Down(NotizAuftrag)).Update([Down(NotizAuftrag)], n => Columns(n, "ID"));

        using (UiCulture.Use("en"))
        {
            var code = await BuildAsync(state, QueryCodeOptions.For("Auftrag") with { ClassName = "1x" });

            Assert.Contains("public static class AuftragQueries", code.Code, StringComparison.Ordinal);
            Assert.Contains("    Nr = a.Nr,", code.Code, StringComparison.Ordinal);
            Assert.Equal(
            [
                "“1x” is not a C# name – AuftragQueries is used instead.",
                "AUFTRAG → NOTIZ: no entity of the C# model – only in the SQL.",
                "“nicht gültig!” is not a C# name – Nr is used instead.",
            ], code.Notes);
        }
    }
}
