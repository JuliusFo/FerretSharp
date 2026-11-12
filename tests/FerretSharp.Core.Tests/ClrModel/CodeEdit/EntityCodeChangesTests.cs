using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Design;
using FerretSharp.Core.Schema;
using static FerretSharp.Core.Tests.ClrModel.CodeGenerationModel;

namespace FerretSharp.Core.Tests.ClrModel;

/// <summary>Designer changes of a mapped table in the entity and its configuration (WP-41, first stage).</summary>
public sealed class EntityCodeChangesTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "work-clone");

    private static readonly string Entity = """
        namespace Shop;

        public class Kunde
        {
            public int KundeId { get; set; }

            /// <summary>The name.</summary>
            [MaxLength(100)]
            public string Name { get; set; } = "";
            public DateTime ErstelltAm { get; set; }
            public decimal? Umsatz { get; set; }
            public bool Gesperrt { get; set; }
            [Required, MaxLength(20)]
            public KundeStatus? Status { get; set; }
            public string? Notiz { get; set; }

            public ICollection<Auftrag> Auftraege { get; set; } = [];
        }
        """.ReplaceLineEndings("\n");

    private static readonly string Configurations = """
        using Microsoft.EntityFrameworkCore;

        namespace Shop.Data;

        public sealed class AuftragConfiguration : IEntityTypeConfiguration<Auftrag>
        {
            public void Configure(EntityTypeBuilder<Auftrag> builder)
            {
                builder.Property(a => a.Notiz).HasMaxLength(10);
            }
        }

        public sealed class KundeConfiguration : IEntityTypeConfiguration<Kunde>
        {
            public void Configure(EntityTypeBuilder<Kunde> b)
            {
                b.ToTable("KUNDEN");
                b.Property(k => k.Name).HasMaxLength(100).IsRequired();
                b.Property(k => k.Umsatz).HasPrecision(12, 2);
                b.Property(k => k.Gesperrt).HasConversion<JaNeinConverter>();
                b.HasIndex(k => new { k.Name, k.Notiz });
            }
        }
        """.ReplaceLineEndings("\n");

    private static CodeScan Scan(string? entity = null, string? configurations = null) =>
        CodeScan.Of(Root, [(Path.Combine("Entities", "Kunde.cs"), entity ?? Entity), (Path.Combine("Data", "Configurations.cs"), configurations ?? Configurations)]);

    private static string TextOf(CodeChangePlan plan, string file) => plan.Files.Single(f => f.Relative.EndsWith(file, StringComparison.Ordinal)).NewText;

    private static ColumnInfo Column(string name) => Kunden.Columns.Single(c => c.Name == name);

    [Fact]
    public async Task A_new_column_goes_behind_the_last_property_and_its_line_behind_the_last_property_line_written_as_the_project_writes()
    {
        var mapping = await MappingAsync();
        var rabatt = new ColumnInfo("RABATT", "NUMBER", null, false, 5, 2, true, false, null, 13);
        var draft = TableDraft.Of(Kunden) with { Columns = [.. TableDraft.Of(Kunden).Columns, new ColumnDraft(null, rabatt)] };

        var plan = EntityCodeChanges.Plan(mapping, Kunden.Table.Ref, ColumnChanges.Of(Kunden, draft), Scan());

        Assert.True(plan.CanWrite, string.Join("\n", plan.Problems));
        Assert.Equal(Entity.Replace("    public string? Notiz { get; set; }\n", "    public string? Notiz { get; set; }\n    public decimal? Rabatt { get; set; }\n", StringComparison.Ordinal),
            TextOf(plan, "Kunde.cs"));
        Assert.Equal(Configurations.Replace(
            "        b.Property(k => k.Gesperrt).HasConversion<JaNeinConverter>();\n",
            "        b.Property(k => k.Gesperrt).HasConversion<JaNeinConverter>();\n        b.Property(k => k.Rabatt).HasPrecision(5, 2);\n", StringComparison.Ordinal),
            TextOf(plan, "Configurations.cs"));
    }

    [Fact]
    public async Task Changed_facets_adjust_what_the_project_configures_and_nothing_else()
    {
        var mapping = await MappingAsync();
        var draft = TableDraft.Of(Kunden);
        draft = draft with
        {
            Columns = [.. draft.Columns.Select(c => c.Original switch
            {
                "NAME" => c with { Column = c.Column with { Length = 150, Nullable = true } },
                "UMSATZ" => c with { Column = c.Column with { Precision = 14, Scale = 3 } },
                "ERSTELLT_AM" => c with { Column = c.Column with { Nullable = true } }, // nothing configured but the type
                "STATUS" => c with { Column = c.Column with { Length = 30 } }, // nullable already: [Required] stays (review WP-41)
                _ => c,
            })],
        };

        var plan = EntityCodeChanges.Plan(mapping, Kunden.Table.Ref, ColumnChanges.Of(Kunden, draft), Scan());

        Assert.True(plan.CanWrite, string.Join("\n", plan.Problems));
        Assert.Equal(Entity
                .Replace("[MaxLength(100)]", "[MaxLength(150)]", StringComparison.Ordinal)
                .Replace("public string Name", "public string? Name", StringComparison.Ordinal)
                .Replace("public DateTime ErstelltAm", "public DateTime? ErstelltAm", StringComparison.Ordinal)
                .Replace("[Required, MaxLength(20)]", "[Required, MaxLength(30)]", StringComparison.Ordinal),
            TextOf(plan, "Kunde.cs"));
        Assert.Equal(Configurations
                .Replace("b.Property(k => k.Name).HasMaxLength(100).IsRequired();", "b.Property(k => k.Name).HasMaxLength(150);", StringComparison.Ordinal)
                .Replace("HasPrecision(12, 2)", "HasPrecision(14, 3)", StringComparison.Ordinal),
            TextOf(plan, "Configurations.cs"));
        Assert.Empty(plan.Notes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)] // after the DDL the model is laid over the new schema: NAME is no longer mapped (E2E of WP-41)
    public async Task A_dropped_column_takes_its_property_with_comment_and_attribute_and_its_lines_and_names_what_still_uses_it(bool remapped)
    {
        var mapping = await MappingAsync(dropped: remapped ? "NAME" : null);
        var draft = TableDraft.Of(Kunden);
        draft = draft with { Columns = [.. draft.Columns.Select(c => c.Original is "NAME" or "MANDANT_ID" ? c with { Dropped = true } : c)] };

        var plan = EntityCodeChanges.Plan(mapping, Kunden.Table.Ref, ColumnChanges.Of(Kunden, draft), Scan());

        Assert.True(plan.CanWrite, string.Join("\n", plan.Problems));
        Assert.Equal(Entity.Replace("""
                /// <summary>The name.</summary>
                [MaxLength(100)]
                public string Name { get; set; } = "";

            """.ReplaceLineEndings("\n"), "", StringComparison.Ordinal), TextOf(plan, "Kunde.cs"));
        Assert.Equal(Configurations.Replace("        b.Property(k => k.Name).HasMaxLength(100).IsRequired();\n", "", StringComparison.Ordinal), TextOf(plan, "Configurations.cs"));
        // MANDANT_ID is a shadow property: nothing declared, nothing configured – no note.
        Assert.Equal([$"Name steht noch in {Path.Combine("Data", "Configurations.cs")}, Zeile 21: b.HasIndex(k => new {{ k.Name, k.Notiz }});"], plan.Notes);
    }

    [Fact]
    public async Task A_project_without_nullable_reference_types_gets_no_question_mark_on_strings_and_keeps_tabs_and_crlf()
    {
        var mapping = await MappingAsync();
        var entity = "namespace Shop\r\n{\r\n\tpublic class Kunde\r\n\t{\r\n\t\tpublic int KundeId { get; set; }\r\n\t\tpublic string Notiz { get; set; }\r\n\t}\r\n}\r\n";
        var configurations = "namespace Shop\r\n{\r\n\tpublic class KundeConfiguration : IEntityTypeConfiguration<Kunde>\r\n\t{\r\n\t\tpublic void Configure(EntityTypeBuilder<Kunde> builder)\r\n\t\t{\r\n\t\t}\r\n\t}\r\n}\r\n";
        var bemerkung = new ColumnInfo("BEMERKUNG", "VARCHAR2", 40, true, null, null, true, false, null, 13);
        var draft = TableDraft.Of(Kunden) with { Columns = [.. TableDraft.Of(Kunden).Columns, new ColumnDraft(null, bemerkung)] };

        var plan = EntityCodeChanges.Plan(mapping, Kunden.Table.Ref, ColumnChanges.Of(Kunden, draft), Scan(entity, configurations));

        Assert.Equal(entity.Replace("\t\tpublic string Notiz { get; set; }\r\n", "\t\tpublic string Notiz { get; set; }\r\n\t\tpublic string Bemerkung { get; set; }\r\n", StringComparison.Ordinal),
            TextOf(plan, "Kunde.cs"));
        Assert.Equal(configurations.Replace("\t\t{\r\n\t\t}", "\t\t{\r\n\t\t\tbuilder.Property(x => x.Bemerkung).HasMaxLength(40);\r\n\t\t}", StringComparison.Ordinal),
            TextOf(plan, "Configurations.cs"));
    }

    [Fact]
    public async Task OnModelCreating_configures_through_a_lambda_or_a_chain_on_the_entity()
    {
        var mapping = await MappingAsync();
        var context = """
            namespace Shop;

            public class Ctx : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Kunde>(e =>
                    {
                        e.Property(x => x.Name).HasColumnType("VARCHAR2(100 CHAR)");
                    });
                    modelBuilder.Entity<Kunde>().Property(x => x.Umsatz).HasColumnType("number(12, 2)");
                }
            }
            """.ReplaceLineEndings("\n");
        var draft = TableDraft.Of(Kunden);
        draft = draft with
        {
            Columns = [.. draft.Columns.Select(c => c.Original switch
            {
                "NAME" => c with { Column = c.Column with { Length = 80, CharSemantics = false } },
                "UMSATZ" => c with { Column = c.Column with { Precision = 15 } },
                _ => c,
            })],
        };

        var plan = EntityCodeChanges.Plan(mapping, Kunden.Table.Ref, ColumnChanges.Of(Kunden, draft), Scan(configurations: context));

        Assert.Equal(context
                .Replace("\"VARCHAR2(100 CHAR)\"", "\"VARCHAR2(80 BYTE)\"", StringComparison.Ordinal)
                .Replace("\"number(12, 2)\"", "\"number(15, 2)\"", StringComparison.Ordinal),
            TextOf(plan, "Configurations.cs"));
    }

    [Fact]
    public async Task Renames_type_changes_and_facets_the_project_does_not_configure_are_named_not_changed()
    {
        var mapping = await MappingAsync();
        var draft = TableDraft.Of(Kunden);
        draft = draft with
        {
            Columns = [.. draft.Columns.Select(c => c.Original switch
            {
                "NOTIZ" => c with { Column = c.Column with { Name = "BEMERKUNG" } },
                "ERSTELLT_AM" => c with { Column = c.Column with { DataType = "TIMESTAMP(6)" } },
                "MANDANT_ID" => c with { Column = c.Column with { Precision = 12 } }, // an int no longer holds it
                _ => c,
            })],
        };

        var plan = EntityCodeChanges.Plan(mapping, Kunden.Table.Ref, ColumnChanges.Of(Kunden, draft), Scan());

        Assert.Empty(plan.Files);
        Assert.Equal(
        [
            "ERSTELLT_AM ist jetzt TIMESTAMP(6): den Typ von ErstelltAm (DateTime) bitte von Hand prüfen.",
            "MandantId: int fasst eventuell nicht jeden Wert von NUMBER(12) – EF liest die Spalte als long.",
            "NOTIZ → BEMERKUNG: Umbenennungen werden im Code noch nicht angepasst – Notiz (HasColumnName) bitte von Hand ändern.",
        ], plan.Notes);
    }

    /// <summary>Review WP-41, K11: a precision without scale got no scale, and a member sharing its line went with a dropped one.</summary>
    [Fact]
    public async Task A_precision_without_scale_gets_it_and_a_member_on_the_same_line_stays()
    {
        var mapping = await MappingAsync();
        var entity = Entity.Replace("    public int KundeId { get; set; }\n", "    public int KundeId { get; set; } public bool? Geprueft { get; set; }\n", StringComparison.Ordinal)
            .Replace("    public DateTime ErstelltAm { get; set; }\n", "    public DateTime ErstelltAm { get; set; } public string? Notiz2 { get; set; }\n", StringComparison.Ordinal);
        var configurations = Configurations.Replace("HasPrecision(12, 2)", "HasPrecision(12)", StringComparison.Ordinal);
        var draft = TableDraft.Of(Kunden);
        draft = draft with
        {
            Columns = [.. draft.Columns.Select(c => c.Original switch
            {
                "UMSATZ" => c with { Column = c.Column with { Scale = 3 } },
                "GEPRUEFT" => c with { Dropped = true },
                _ => c,
            })],
        };

        var plan = EntityCodeChanges.Plan(mapping, Kunden.Table.Ref, ColumnChanges.Of(Kunden, draft), Scan(entity, configurations));

        Assert.Equal(configurations.Replace("HasPrecision(12)", "HasPrecision(12, 3)", StringComparison.Ordinal), TextOf(plan, "Configurations.cs"));
        Assert.Equal(entity.Replace(" public bool? Geprueft { get; set; }", "", StringComparison.Ordinal), TextOf(plan, "Kunde.cs"));
    }

    /// <summary>Review WP-41, M3: the guard against writing twice asked for the plain name, which a navigation had taken.</summary>
    [Fact]
    public async Task A_new_column_whose_name_is_taken_gets_the_unique_name_and_is_added()
    {
        var mapping = await MappingAsync();
        var column = new ColumnInfo("NAME_", "VARCHAR2", 10, true, null, null, true, false, null, 13); // PascalCase: Name, taken
        var draft = TableDraft.Of(Kunden) with { Columns = [.. TableDraft.Of(Kunden).Columns, new ColumnDraft(null, column)] };

        var plan = EntityCodeChanges.Plan(mapping, Kunden.Table.Ref, ColumnChanges.Of(Kunden, draft), Scan());

        Assert.Contains("    public string? Name2 { get; set; }\n", TextOf(plan, "Kunde.cs"), StringComparison.Ordinal);
        Assert.Contains("        b.Property(k => k.Name2).HasColumnName(\"NAME_\").HasMaxLength(10);\n", TextOf(plan, "Configurations.cs"), StringComparison.Ordinal);
    }

    /// <summary>Review WP-41, M5: the same class in two projects of the clone was changed in both.</summary>
    [Fact]
    public async Task A_class_declared_twice_without_partial_is_a_problem_and_nothing_is_written()
    {
        var mapping = await MappingAsync();
        var draft = TableDraft.Of(Kunden);
        draft = draft with { Columns = [.. draft.Columns.Select(c => c.Original == "NAME" ? c with { Dropped = true } : c)] };
        var scan = CodeScan.Of(Root, [(Path.Combine("Entities", "Kunde.cs"), Entity), (Path.Combine("Old", "Kunde.cs"), "namespace Shop;\npublic class Kunde { public string Name { get; set; } = \"\"; }\n")]);

        var plan = EntityCodeChanges.Plan(mapping, Kunden.Table.Ref, ColumnChanges.Of(Kunden, draft), scan);

        Assert.False(plan.CanWrite);
        Assert.Equal([$"Shop.Kunde ist im Arbeits-Klon mehrfach deklariert, nicht als partial class ({Path.Combine("Entities", "Kunde.cs")}, {Path.Combine("Old", "Kunde.cs")}) – unklar, welche das Modell meint. Nichts wird geschrieben."], plan.Problems);
    }

    [Fact]
    public async Task Without_the_class_in_the_work_clone_nothing_can_be_written()
    {
        var mapping = await MappingAsync();
        var draft = TableDraft.Of(Kunden);
        draft = draft with { Columns = [.. draft.Columns.Select(c => c.Original == "NAME" ? c with { Dropped = true } : c)] };

        var plan = EntityCodeChanges.Plan(mapping, Kunden.Table.Ref, ColumnChanges.Of(Kunden, draft), Scan(entity: Entity.Replace("namespace Shop;", "namespace Other;", StringComparison.Ordinal)));

        Assert.False(plan.CanWrite);
        Assert.Equal(["Die Klasse Shop.Kunde ist nicht im Arbeits-Klon."], plan.Problems);
    }
}
