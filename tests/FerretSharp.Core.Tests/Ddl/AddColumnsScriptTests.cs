using FerretSharp.Core.Ddl;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Tests.Ddl;

public class AddColumnsScriptTests
{
    private static readonly TableRef Kunde = new("SHOP", "KUNDE");

    private static ColumnInfo Column(string name, string type, int position, int? length = null, int? precision = null, bool nullable = true,
        string? @default = null, string? comment = null, bool isVirtual = false) =>
        new(name, type, length, true, precision, null, nullable, false, @default, position, comment, isVirtual);

    [Fact]
    public void Columns_are_added_in_schema_order_with_their_comments_after_them()
    {
        var script = AddColumnsScript.Write(Kunde, Kunde,
        [
            Column("LAND", "VARCHAR2", 7, length: 2, @default: "'DE'", comment: "ISO-Code"),
            Column("FAX", "VARCHAR2", 3, length: 30, comment: "Fax-Nummer des Kunden"),
        ]);

        Assert.Equal(
            """
            -- Spalten aus SHOP.KUNDE, zum Hinzufügen in dieselbe Tabelle.

            ALTER TABLE "SHOP"."KUNDE" ADD ("FAX" VARCHAR2(30 CHAR));

            ALTER TABLE "SHOP"."KUNDE" ADD ("LAND" VARCHAR2(2 CHAR) DEFAULT 'DE');

            COMMENT ON COLUMN "SHOP"."KUNDE"."FAX" IS 'Fax-Nummer des Kunden';

            COMMENT ON COLUMN "SHOP"."KUNDE"."LAND" IS 'ISO-Code';
            """.ReplaceLineEndings("\n"), script);
    }

    [Fact]
    public void A_mandatory_column_without_default_is_written_with_the_warning_of_the_comparison()
    {
        var script = AddColumnsScript.Write(Kunde, Kunde, [Column("NR", "NUMBER", 2, precision: 10, nullable: false)]);

        Assert.Contains("-- ⚠ Schlägt fehl, wenn die Tabelle Zeilen hat (NOT NULL ohne Default).\nALTER TABLE \"SHOP\".\"KUNDE\" ADD (\"NR\" NUMBER(10) NOT NULL);", script);
    }

    [Fact]
    public void A_virtual_column_follows_the_column_it_is_computed_from()
    {
        var script = AddColumnsScript.Write(Kunde, Kunde,
        [
            Column("NAME_GROSS", "VARCHAR2", 5, length: 50, @default: "UPPER(\"NAME\")", isVirtual: true),
            Column("NAME", "VARCHAR2", 4, length: 50),
        ]);

        Assert.True(script.IndexOf("ADD (\"NAME\" ", StringComparison.Ordinal) < script.IndexOf("ADD (\"NAME_GROSS\" ", StringComparison.Ordinal));
        Assert.Contains("GENERATED ALWAYS AS (UPPER(\"NAME\")) VIRTUAL", script);
    }

    [Fact]
    public void The_target_may_have_another_owner()
    {
        var script = AddColumnsScript.Write(Kunde, new TableRef("SHOP_PROD", "KUNDE"), [Column("FAX", "VARCHAR2", 3, length: 30, comment: "Fax")]);

        Assert.Contains("-- Spalten aus SHOP.KUNDE,", script);
        Assert.Contains("ALTER TABLE \"SHOP_PROD\".\"KUNDE\" ADD (\"FAX\" VARCHAR2(30 CHAR));", script);
        Assert.Contains("COMMENT ON COLUMN \"SHOP_PROD\".\"KUNDE\".\"FAX\" IS 'Fax';", script);
    }
}
