using FerretSharp.Core.Ddl;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Tests.Ddl;

public class AddColumnsScriptTests
{
    private static readonly TableRef Kunde = new("SHOP", "KUNDE");

    private static ColumnInfo Column(string name, string type, int position, int? length = null, int? precision = null, bool nullable = true,
        string? @default = null, string? comment = null, bool isVirtual = false) =>
        new(name, type, length, true, precision, null, nullable, false, @default, position, comment, isVirtual);

    private static ConstraintInfo ForeignKey(string name, string[] columns, TableRef to, string[] toColumns, string deleteRule = "NO ACTION", bool generatedName = false) =>
        new(name, ConstraintType.ForeignKey, columns, null, to, toColumns, deleteRule, true, true, false, false, generatedName);

    [Fact]
    public void Columns_are_added_in_schema_order_with_their_comments_after_them()
    {
        var script = AddColumnsScript.Write(Kunde, Kunde,
        [
            Column("LAND", "VARCHAR2", 7, length: 2, @default: "'DE'", comment: "ISO-Code"),
            Column("FAX", "VARCHAR2", 3, length: 30, comment: "Fax-Nummer des Kunden"),
        ], []);

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
        var script = AddColumnsScript.Write(Kunde, Kunde, [Column("NR", "NUMBER", 2, precision: 10, nullable: false)], []);

        Assert.Contains("-- ⚠ Schlägt fehl, wenn die Tabelle Zeilen hat (NOT NULL ohne Default).\nALTER TABLE \"SHOP\".\"KUNDE\" ADD (\"NR\" NUMBER(10) NOT NULL);", script);
    }

    [Fact]
    public void A_virtual_column_follows_the_column_it_is_computed_from()
    {
        var script = AddColumnsScript.Write(Kunde, Kunde,
        [
            Column("NAME_GROSS", "VARCHAR2", 5, length: 50, @default: "UPPER(\"NAME\")", isVirtual: true),
            Column("NAME", "VARCHAR2", 4, length: 50),
        ], []);

        Assert.True(script.IndexOf("ADD (\"NAME\" ", StringComparison.Ordinal) < script.IndexOf("ADD (\"NAME_GROSS\" ", StringComparison.Ordinal));
        Assert.Contains("GENERATED ALWAYS AS (UPPER(\"NAME\")) VIRTUAL", script);
    }

    [Fact]
    public void The_target_may_have_another_owner()
    {
        var script = AddColumnsScript.Write(Kunde, new TableRef("SHOP_PROD", "KUNDE"), [Column("FAX", "VARCHAR2", 3, length: 30, comment: "Fax")], []);

        Assert.Contains("-- Spalten aus SHOP.KUNDE,", script);
        Assert.Contains("ALTER TABLE \"SHOP_PROD\".\"KUNDE\" ADD (\"FAX\" VARCHAR2(30 CHAR));", script);
        Assert.Contains("COMMENT ON COLUMN \"SHOP_PROD\".\"KUNDE\".\"FAX\" IS 'Fax';", script);
    }

    /// <summary>
    /// A foreign key made only of added columns comes along after them, before the comments: a table of the source's owner is
    /// the one of the target's owner, the table itself the target table, another owner's stays. One that needs a column that is
    /// not added stays out (it would fail, or need a column the target may lack).
    /// </summary>
    [Fact]
    public void Foreign_keys_of_only_added_columns_come_along_in_the_target_s_owner()
    {
        var target = new TableRef("SHOP_PROD", "KUNDE");
        var script = AddColumnsScript.Write(Kunde, target,
        [
            Column("LAND", "VARCHAR2", 7, length: 2, comment: "ISO-Code"),
            Column("WERBER_ID", "NUMBER", 8, precision: 10),
            Column("FILIALE_ID", "NUMBER", 9, precision: 10),
        ],
        [
            ForeignKey("FK_KUNDE_LAND", ["LAND"], new TableRef("SHOP", "LAND"), ["CODE"], deleteRule: "SET NULL"),
            ForeignKey("SYS_C0012345", ["WERBER_ID"], Kunde, ["ID"], generatedName: true),
            ForeignKey("FK_KUNDE_FILIALE", ["FILIALE_ID"], new TableRef("STAMM", "FILIALE"), ["ID"]),
            ForeignKey("FK_KUNDE_ADRESSE", ["LAND", "PLZ"], new TableRef("SHOP", "ADRESSE"), ["LAND", "PLZ"]),
            new ConstraintInfo("UK_KUNDE_LAND", ConstraintType.Unique, ["LAND"], null, null, [], null, true, true, false, false, false),
        ]);

        Assert.Equal(
            """
            -- Spalten aus SHOP.KUNDE, zum Hinzufügen in dieselbe Tabelle.

            ALTER TABLE "SHOP_PROD"."KUNDE" ADD ("LAND" VARCHAR2(2 CHAR));

            ALTER TABLE "SHOP_PROD"."KUNDE" ADD ("WERBER_ID" NUMBER(10));

            ALTER TABLE "SHOP_PROD"."KUNDE" ADD ("FILIALE_ID" NUMBER(10));

            ALTER TABLE "SHOP_PROD"."KUNDE" ADD CONSTRAINT "FK_KUNDE_LAND" FOREIGN KEY ("LAND") REFERENCES "SHOP_PROD"."LAND" ("CODE") ON DELETE SET NULL;

            ALTER TABLE "SHOP_PROD"."KUNDE" ADD FOREIGN KEY ("WERBER_ID") REFERENCES "SHOP_PROD"."KUNDE" ("ID");

            ALTER TABLE "SHOP_PROD"."KUNDE" ADD CONSTRAINT "FK_KUNDE_FILIALE" FOREIGN KEY ("FILIALE_ID") REFERENCES "STAMM"."FILIALE" ("ID");

            COMMENT ON COLUMN "SHOP_PROD"."KUNDE"."LAND" IS 'ISO-Code';
            """.ReplaceLineEndings("\n"), script);
    }

    /// <summary>New columns hold NULL, so their foreign key checks nothing – unless a default (or identity) gives them values.</summary>
    [Fact]
    public void A_foreign_key_on_a_column_with_default_warns_that_it_checks_the_rows()
    {
        var script = AddColumnsScript.Write(Kunde, Kunde, [Column("LAND", "VARCHAR2", 7, length: 2, @default: "'DE'")],
            [ForeignKey("FK_KUNDE_LAND", ["LAND"], new TableRef("SHOP", "LAND"), ["CODE"])]);

        Assert.Contains("-- ⚠ Prüft die vorhandenen Zeilen und scheitert bei Verstößen.\nALTER TABLE \"SHOP\".\"KUNDE\" ADD CONSTRAINT \"FK_KUNDE_LAND\"", script);
    }

    [Fact]
    public void Constraints_that_could_not_be_read_are_named_in_the_header()
    {
        var script = AddColumnsScript.Write(Kunde, Kunde, [Column("FAX", "VARCHAR2", 3, length: 30)], null);

        Assert.StartsWith("-- Spalten aus SHOP.KUNDE, zum Hinzufügen in dieselbe Tabelle.\n-- Ohne Fremdschlüssel: Die Constraints der Tabelle ließen sich nicht lesen.\n\nALTER TABLE", script);
    }

    /// <summary>Review 3.54.0, A15: an identity ALWAYS was written BY DEFAULT – the target took explicit ids the source refuses.</summary>
    [Fact]
    public void An_identity_keeps_its_kind()
    {
        ColumnInfo Identity(string name, bool always) =>
            Column(name, "NUMBER", 1, precision: 10, nullable: false) with { IsIdentity = true, IdentityAlways = always };

        var script = AddColumnsScript.Write(Kunde, Kunde, [Identity("ID", always: true)], []);

        Assert.Contains("ADD (\"ID\" NUMBER(10) GENERATED ALWAYS AS IDENTITY)", script);
        Assert.Contains("ADD (\"NR\" NUMBER(10) GENERATED BY DEFAULT AS IDENTITY)", AddColumnsScript.Write(Kunde, Kunde, [Identity("NR", always: false)], []));
    }

    /// <summary>
    /// Review 3.54.0, A11: a quoted Oracle name with a line break in the header made the rest of it an executable line of the
    /// script opened in the SQL editor of another connection.
    /// </summary>
    [Fact]
    public void A_line_break_in_the_source_name_stays_in_the_comment()
    {
        var odd = new TableRef("SHOP", "X\nDROP TABLE Y;\r--");

        var script = AddColumnsScript.Write(odd, Kunde, [Column("FAX", "VARCHAR2", 3, length: 30)], []);

        Assert.Contains("\n-- DROP TABLE Y;\n", script);
        Assert.DoesNotContain("\nDROP", script);
    }
}
