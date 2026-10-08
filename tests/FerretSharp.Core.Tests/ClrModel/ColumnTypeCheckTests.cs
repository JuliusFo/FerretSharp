using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Tests.ClrModel;

public sealed class ColumnTypeCheckTests
{
    private static readonly TableSummary Table = new("APP", "KUNDEN", TableKind.Table);
    private static readonly TableSummary View = new("APP", "V_KUNDEN", TableKind.View);

    private static PropertyExport Prop(string clrType, bool nullable = false, string? provider = null, string? converter = null,
        int? maxLength = null, int? precision = null, int? scale = null, bool? columnNullable = null) =>
        new("P", clrType, clrType, nullable, false, "C", null, converter, provider, false, null,
            MaxLength: maxLength, Precision: precision, Scale: scale, ColumnNullable: columnNullable);

    private static ColumnInfo Col(string dataType, int? length = null, bool charSemantics = false, int? precision = null, int? scale = null,
        bool nullable = false, bool identity = false, bool defaultOnNull = false) =>
        new("C", dataType, length, charSemantics, precision, scale, nullable, identity, null, 1, DefaultOnNull: defaultOnNull);

    private static List<(ColumnMismatchKind Kind, MismatchSeverity Severity)> Check(PropertyExport property, ColumnInfo column, bool view = false)
    {
        var entity = new EntityExport("Shop.Kunde", "Shop.Kunde", false, null, "KUNDEN", null, null, null, [property], [], []);
        return ColumnTypeCheck.Check(entity, property, view ? View : Table, column, readOnly: view).Select(m => (m.Kind, m.Severity)).ToList();
    }

    [Fact]
    public void Matching_types_give_nothing()
    {
        Assert.Empty(Check(Prop("int"), Col("NUMBER", precision: 10, scale: 0)));
        Assert.Empty(Check(Prop("string", maxLength: 100), Col("VARCHAR2", 100, charSemantics: true)));
        Assert.Empty(Check(Prop("string", nullable: true), Col("CLOB", nullable: true)));
        Assert.Empty(Check(Prop("decimal", nullable: true, precision: 12, scale: 2), Col("NUMBER", precision: 12, scale: 2, nullable: true)));
        Assert.Empty(Check(Prop("DateTime"), Col("DATE")));
        Assert.Empty(Check(Prop("DateTimeOffset"), Col("TIMESTAMP(6) WITH TIME ZONE", scale: 6)));
        Assert.Empty(Check(Prop("Guid"), Col("RAW", 16)));
        Assert.Empty(Check(Prop("byte[]", nullable: true), Col("BLOB", nullable: true)));
        Assert.Empty(Check(Prop("bool"), Col("NUMBER", precision: 1, scale: 0)));
        // A converter decides what is stored: bool as 'J'/'N', an enum as its number.
        Assert.Empty(Check(Prop("bool", provider: "string", converter: "JaNeinConverter", maxLength: 1), Col("CHAR", 1)));
        Assert.Empty(Check(Prop("Kundenart", provider: "int"), Col("NUMBER", precision: 2, scale: 0)));
        // NUMBER without precision is common for keys: not reported (the provider maps int to NUMBER(10) itself).
        Assert.Empty(Check(Prop("int"), Col("NUMBER")));
        // XMLTYPE and unknown CLR types: nothing reliable to say.
        Assert.Empty(Check(Prop("string"), Col("XMLTYPE")));
        Assert.Empty(Check(Prop("Point"), Col("VARCHAR2", 10)));
    }

    [Fact]
    public void Types_that_cannot_be_read_are_errors()
    {
        Assert.Equal([(ColumnMismatchKind.Type, MismatchSeverity.Error)], Check(Prop("string"), Col("NUMBER", precision: 10)));
        Assert.Equal([(ColumnMismatchKind.Type, MismatchSeverity.Error)], Check(Prop("DateTimeOffset"), Col("DATE")));
        Assert.Equal([(ColumnMismatchKind.Type, MismatchSeverity.Error)], Check(Prop("int"), Col("VARCHAR2", 10)));

        var entity = new EntityExport("Shop.Kunde", "Shop.Kunde", false, null, "KUNDEN", null, null, null, [], [], []);
        var forgotten = Assert.Single(ColumnTypeCheck.Check(entity, Prop("bool"), Table, Col("CHAR", 1), readOnly: false));
        Assert.Contains("fehlt der Converter", forgotten.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Null_on_one_side_only_is_reported_for_reading_and_writing()
    {
        Assert.Equal([(ColumnMismatchKind.Nullability, MismatchSeverity.Warning)], Check(Prop("int"), Col("NUMBER", precision: 10, scale: 0, nullable: true)));
        Assert.Equal([(ColumnMismatchKind.Nullability, MismatchSeverity.Warning)], Check(Prop("int", nullable: true), Col("NUMBER", precision: 10, scale: 0)));
        // Optional strings and byte[] on NOT NULL columns are fine on Oracle: '' is NULL there, so "required" would not
        // keep NULL out either. A converted bool? still can write NULL.
        Assert.Empty(Check(Prop("string", nullable: true), Col("VARCHAR2", 50, charSemantics: true)));
        Assert.Empty(Check(Prop("byte[]", nullable: true), Col("RAW", 16)));
        Assert.Equal([(ColumnMismatchKind.Nullability, MismatchSeverity.Warning)],
            Check(Prop("bool", nullable: true, provider: "string", converter: "JaNeinConverter"), Col("CHAR", 1)));
        // The other way round stays: a required string reading NULL throws.
        Assert.Equal([(ColumnMismatchKind.Nullability, MismatchSeverity.Warning)], Check(Prop("string"), Col("VARCHAR2", 50, charSemantics: true, nullable: true)));
        // Generated by the database: NULL from the model is fine.
        Assert.Empty(Check(Prop("int", nullable: true), Col("NUMBER", precision: 10, scale: 0, identity: true)));
        Assert.Empty(Check(Prop("int", nullable: true), Col("NUMBER", precision: 10, scale: 0, defaultOnNull: true)));
        // EF's column view counts: a required property of a derived type in a TPH table has a nullable column.
        Assert.Empty(Check(Prop("int", columnNullable: true), Col("NUMBER", precision: 10, scale: 0, nullable: true)));
        // Views are only read, and Oracle reports computed view columns as nullable: a hint.
        Assert.Equal([(ColumnMismatchKind.Nullability, MismatchSeverity.Hint)], Check(Prop("int"), Col("NUMBER", precision: 10, scale: 0, nullable: true), view: true));
        Assert.Empty(Check(Prop("int", nullable: true), Col("NUMBER", precision: 10, scale: 0), view: true));
    }

    [Fact]
    public void Lengths_compare_characters_and_bytes()
    {
        Assert.Equal([(ColumnMismatchKind.Length, MismatchSeverity.Warning)], Check(Prop("string", maxLength: 100), Col("VARCHAR2", 50, charSemantics: true)));
        Assert.Equal([(ColumnMismatchKind.Length, MismatchSeverity.Hint)], Check(Prop("string", maxLength: 50), Col("VARCHAR2", 100, charSemantics: true)));
        // BYTE semantics: 3 characters with umlauts need more than 3 bytes; fewer characters than bytes are fine.
        Assert.Equal([(ColumnMismatchKind.Length, MismatchSeverity.Hint)], Check(Prop("string", maxLength: 3), Col("CHAR", 3)));
        Assert.Empty(Check(Prop("string", maxLength: 20), Col("VARCHAR2", 80)));
        Assert.Equal([(ColumnMismatchKind.Length, MismatchSeverity.Warning)], Check(Prop("string", maxLength: 30), Col("NVARCHAR2", 20)));
        Assert.Equal([(ColumnMismatchKind.Length, MismatchSeverity.Warning)], Check(Prop("byte[]", maxLength: 32), Col("RAW", 16)));
        // Without a configured length there is nothing to compare; views are not written.
        Assert.Empty(Check(Prop("string"), Col("VARCHAR2", 10)));
        Assert.Empty(Check(Prop("string", maxLength: 100), Col("VARCHAR2", 50, charSemantics: true), view: true));
    }

    [Fact]
    public void Numbers_compare_digits_and_scale()
    {
        Assert.Equal([(ColumnMismatchKind.Precision, MismatchSeverity.Warning)], Check(Prop("int"), Col("NUMBER", precision: 12, scale: 2)));
        Assert.Equal([(ColumnMismatchKind.Precision, MismatchSeverity.Warning)], Check(Prop("int"), Col("NUMBER", precision: 12, scale: 0)));
        Assert.Empty(Check(Prop("long"), Col("NUMBER", precision: 12, scale: 0)));
        Assert.Equal([(ColumnMismatchKind.Precision, MismatchSeverity.Warning)], Check(Prop("long"), Col("FLOAT", precision: 126)));
        Assert.Equal([(ColumnMismatchKind.Precision, MismatchSeverity.Warning)], Check(Prop("decimal"), Col("NUMBER", precision: 38, scale: 0)));

        // Configured precision: fewer integer digits in the column fail, fewer decimals round, the rest only differs.
        Assert.Equal([(ColumnMismatchKind.Precision, MismatchSeverity.Warning)], Check(Prop("decimal", precision: 14, scale: 2), Col("NUMBER", precision: 12, scale: 2)));
        Assert.Equal([(ColumnMismatchKind.Precision, MismatchSeverity.Warning)], Check(Prop("decimal", precision: 12, scale: 4), Col("NUMBER", precision: 12, scale: 2)));
        Assert.Equal([(ColumnMismatchKind.Precision, MismatchSeverity.Hint)], Check(Prop("decimal", precision: 10, scale: 2), Col("NUMBER", precision: 12, scale: 2)));
        // Saving does not matter on a view.
        Assert.Equal([(ColumnMismatchKind.Precision, MismatchSeverity.Hint)], Check(Prop("decimal", precision: 14, scale: 2), Col("NUMBER", precision: 12, scale: 2), view: true));
    }

    [Fact]
    public void Description_shows_facets_nullability_and_converter()
    {
        Assert.Equal("string(100)", ColumnTypeCheck.Describe(Prop("string", maxLength: 100)));
        Assert.Equal("decimal(12,2)?", ColumnTypeCheck.Describe(Prop("decimal", nullable: true, precision: 12, scale: 2)));
        Assert.Equal("bool → string(1) (JaNeinConverter)",ColumnTypeCheck.Describe(Prop("bool", provider: "string", converter: "JaNeinConverter", maxLength: 1)));
        Assert.Equal("int?", ColumnTypeCheck.Describe(Prop("int", columnNullable: true)));
    }
}
