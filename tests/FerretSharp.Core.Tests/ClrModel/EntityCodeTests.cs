using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Schema;
using static FerretSharp.Core.Tests.ClrModel.CodeGenerationModel;

namespace FerretSharp.Core.Tests.ClrModel;

/// <summary>Entity and configuration from a table the model has no entity for (WP-23, stage 3).</summary>
public sealed class EntityCodeTests
{
    private static ColumnInfo Col(string name, string type, int? length = null, int? precision = null, int? scale = null, bool nullable = true, int position = 1) =>
        new(name, type, length, false, precision, scale, nullable, false, null, position);

    /// <summary>
    /// An order: its key, a foreign key to the mapped KUNDEN, one to a table without entity, a J/N flag. NUMBER(10) is a long
    /// (more than nine digits), except KUNDE_ID: the project maps that column to an int elsewhere.
    /// </summary>
    private static readonly TableDetails Auftrag = new(new TableSummary(Owner, "AUFTRAG", TableKind.Table),
    [
        Col("AUFTRAG_ID", "NUMBER", precision: 10, scale: 0, nullable: false),
        Col("KUNDE_ID", "NUMBER", precision: 10, scale: 0, nullable: false, position: 2),
        Col("LAGER_ID", "NUMBER", precision: 10, scale: 0, position: 3),
        Col("BETRAG", "NUMBER", precision: 12, scale: 2, position: 4),
        Col("BEZAHLT", "CHAR", 1, nullable: false, position: 5),
        Col("NOTIZ", "VARCHAR2", 200, position: 6),
    ], ["AUFTRAG_ID"], [], false);

    private static readonly ForeignKeyInfo ToKunde = new("FK_AUFTRAG_KUNDE", Auftrag.Table.Ref, ["KUNDE_ID"], Kunden.Table.Ref, ["KUNDE_ID"], FkSource.Declared);
    private static readonly ForeignKeyInfo ToLager = new("FK_AUFTRAG_LAGER", Auftrag.Table.Ref, ["LAGER_ID"], new TableRef(Owner, "LAGER"), ["LAGER_ID"], FkSource.Declared);

    [Fact]
    public async Task An_entity_with_key_navigation_and_the_projects_converter()
    {
        var mapping = await MappingAsync();

        var code = EntityCode.Write(mapping, Auftrag, [ToKunde, ToLager], "Auftrag");

        Assert.Equal("""
            public class Auftrag
            {
                public long AuftragId { get; set; }
                public int KundeId { get; set; }
                public long? LagerId { get; set; }
                public decimal? Betrag { get; set; }
                public bool Bezahlt { get; set; }
                public string? Notiz { get; set; }

                public Kunde Kunde { get; set; } = null!;
            }
            """.ReplaceLineEndings("\n"), code.Entity);
        Assert.Equal("""
            public class AuftragConfiguration : IEntityTypeConfiguration<Auftrag>
            {
                public void Configure(EntityTypeBuilder<Auftrag> builder)
                {
                    builder.ToTable("AUFTRAG");
                    builder.HasKey(x => x.AuftragId);
                    builder.Property(x => x.Betrag).HasPrecision(12, 2);
                    builder.Property(x => x.Bezahlt).HasConversion<JaNeinConverter>();
                    builder.Property(x => x.Notiz).HasMaxLength(200);
                    builder.HasOne(x => x.Kunde).WithMany().HasForeignKey(x => x.KundeId);
                }
            }
            """.ReplaceLineEndings("\n"), code.Configuration);
        Assert.Contains("FK_AUFTRAG_LAGER verweist auf LAGER, für die es noch keine Entity gibt: keine Navigation.", code.Notes);
        Assert.Contains(code.Notes, n => n.StartsWith("Bezahlt: bool mit JaNeinConverter", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Two_foreign_keys_to_the_same_entity_are_named_after_their_columns_and_a_table_without_key_is_read_only()
    {
        var mapping = await MappingAsync();
        var table = Auftrag with
        {
            Columns = [.. Auftrag.Columns, Col("RECHNUNGSKUNDE_ID", "NUMBER", precision: 10, scale: 0, position: 7)],
            PrimaryKey = [],
        };
        var second = new ForeignKeyInfo("FK_AUFTRAG_RKUNDE", table.Table.Ref, ["RECHNUNGSKUNDE_ID"], Kunden.Table.Ref, ["KUNDE_ID"], FkSource.Declared);

        var code = EntityCode.Write(mapping, table, [ToKunde, second], "Auftrag");

        Assert.Contains("public Kunde Kunde { get; set; } = null!;", code.Entity, StringComparison.Ordinal); // KundeId → Kunde
        Assert.Contains("public Kunde? Rechnungskunde { get; set; }", code.Entity, StringComparison.Ordinal);
        Assert.Contains("builder.HasNoKey();", code.Configuration, StringComparison.Ordinal);
        Assert.Contains("Die Tabelle hat keinen Primärschlüssel: EF Core kann sie nur lesen (HasNoKey).", code.Notes);
    }

    [Fact]
    public async Task A_composite_key_and_a_view()
    {
        var mapping = await MappingAsync();
        var view = new TableDetails(new TableSummary(Owner, "V_UMSATZ", TableKind.View),
            [Col("JAHR", "NUMBER", precision: 4, scale: 0, nullable: false), Col("MONAT", "NUMBER", precision: 2, scale: 0, nullable: false, position: 2)],
            ["JAHR", "MONAT"], [], false);

        var code = EntityCode.Write(mapping, view, [], "Umsatz");

        Assert.Contains("builder.ToView(\"V_UMSATZ\");", code.Configuration, StringComparison.Ordinal);
        Assert.Contains("builder.HasKey(x => new { x.Jahr, x.Monat });", code.Configuration, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ADRESSEN", "Adresse")] // the project has KUNDEN → Kunde
    [InlineData("AUFTRAG", "Auftrag")]
    [InlineData("AUFTRAG_POSITION", "AuftragPosition")]
    [InlineData("ZEN", "Zen")] // too short to lose the ending
    public async Task Entity_names_follow_the_projects_plural_pattern(string table, string name) =>
        Assert.Equal(name, EntityCode.SuggestName((await MappingAsync()).Model, table));
}
