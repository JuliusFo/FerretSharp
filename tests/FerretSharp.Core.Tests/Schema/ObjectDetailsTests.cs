using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Tests.Schema;

public class ObjectDetailsTests
{
    private static ConstraintInfo Check(string condition, bool generated = true) =>
        new("SYS_C001", ConstraintType.Check, ["NAME"], condition, null, [], null, true, true, false, false, generated);

    [Theory]
    [InlineData("\"NAME\" IS NOT NULL", true)]
    [InlineData("  \"Quoted col\"  is not null ", true)]
    [InlineData("\"MENGE\" > 0", false)]
    [InlineData("\"A\" IS NOT NULL AND \"B\" IS NOT NULL", false)]
    public void Generated_not_null_checks_are_recognized(string condition, bool expected) =>
        Assert.Equal(expected, Check(condition).IsColumnNotNull);

    [Fact]
    public void A_named_not_null_check_is_a_real_constraint() =>
        Assert.False(Check("\"NAME\" IS NOT NULL", generated: false).IsColumnNotNull);

    private static readonly TableRef Auftrag = new("APP", "AUFTRAG");

    private static ForeignKeyInfo Fk(string name, params string[] columns) =>
        new(name, Auftrag, columns, new TableRef("APP", "KUNDEN"), columns, FkSource.Declared);

    private static IndexInfo Index(string name, params IndexColumn[] columns) =>
        new("APP", name, "NORMAL", false, "VALID", columns, "USERS", false, true);

    private static IndexColumn Col(string name, bool descending = false) => new(name, false, descending);

    [Fact]
    public void Foreign_key_is_covered_by_an_index_starting_with_its_columns_in_any_order()
    {
        var fks = new[] { Fk("FK_KUNDE", "KUNDE_ID"), Fk("FK_POS", "MANDANT", "AUFTRAG_NR") };
        var indexes = new[]
        {
            Index("IX_KUNDE_DATUM", Col("KUNDE_ID"), Col("DATUM")),
            Index("IX_POS", Col("AUFTRAG_NR", descending: true), Col("MANDANT")),
        };

        Assert.Empty(IndexAdvice.UnindexedForeignKeys(fks, indexes));
    }

    [Fact]
    public void Index_with_the_columns_further_back_or_as_expression_does_not_count()
    {
        var fks = new[] { Fk("FK_KUNDE", "KUNDE_ID"), Fk("FK_LAND", "LAND") };
        var indexes = new[]
        {
            Index("IX_DATUM_KUNDE", Col("DATUM"), Col("KUNDE_ID")),
            Index("IX_UPPER_LAND", new IndexColumn("UPPER(\"LAND\")", true, false)),
        };

        Assert.Equal(["FK_KUNDE", "FK_LAND"], IndexAdvice.UnindexedForeignKeys(fks, indexes).Select(f => f.Name));
    }

    [Fact]
    public void Composite_foreign_key_needs_all_its_columns_in_front()
    {
        var fk = Fk("FK_POS", "MANDANT", "AUFTRAG_NR");

        Assert.Single(IndexAdvice.UnindexedForeignKeys([fk], [Index("IX_MANDANT", Col("MANDANT"))]));
        Assert.Single(IndexAdvice.UnindexedForeignKeys([fk], [Index("IX_MANDANT_X", Col("MANDANT"), Col("X"), Col("AUFTRAG_NR"))]));
    }
}
