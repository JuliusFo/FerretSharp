using FerretSharp.Core.Ddl;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Tests.Ddl;

public class AddColumnsScriptTests
{
    private static readonly TableRef Kunde = new("SHOP", "KUNDE");

    private static ColumnInfo Column(string name, string type, int position, int? length = null, int? precision = null, bool nullable = true,
        string? @default = null, string? comment = null, bool isVirtual = false) =>
        new(name, type, length, true, precision, null, nullable, false, @default, position, comment, isVirtual);

    private static ConstraintInfo ForeignKey(string name, string[] columns, TableRef? to, string[] toColumns, string deleteRule = "NO ACTION", bool generatedName = false,
        bool enabled = true, bool validated = true, bool deferrable = false, bool deferred = false) =>
        new(name, ConstraintType.ForeignKey, columns, null, to, toColumns, deleteRule, enabled, validated, deferrable, deferred, generatedName);

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

    /// <summary>
    /// New columns hold NULL, so their foreign key checks nothing – unless every key column gets a value at once (default,
    /// identity, virtual): Oracle checks no row with a NULL in a composite key (review, finding 3: one default was enough).
    /// A disabled or not validated key checks no existing row either. Cases: LAND (default) / PLZ (default, identity,
    /// virtual or none) – enabled, validated – warns?
    /// </summary>
    [Theory]
    [InlineData("default", true, true, true)]
    [InlineData("identity", true, true, true)]
    [InlineData("virtual", true, true, true)]
    [InlineData("none", true, true, false)]
    [InlineData("default", false, true, false)]
    [InlineData("default", true, false, false)]
    public void A_foreign_key_warns_only_when_every_column_gets_a_value_and_the_rows_are_checked(string plz, bool enabled, bool validated, bool warns)
    {
        var land = Column("LAND", "VARCHAR2", 7, length: 2, @default: "'DE'");
        var plzColumn = plz switch
        {
            "default" => Column("PLZ", "VARCHAR2", 8, length: 5, @default: "'00000'"),
            "identity" => Column("PLZ", "NUMBER", 8, precision: 5, nullable: false) with { IsIdentity = true },
            "virtual" => Column("PLZ", "VARCHAR2", 8, length: 5, @default: "'0'", isVirtual: true),
            _ => Column("PLZ", "VARCHAR2", 8, length: 5),
        };

        var script = AddColumnsScript.Write(Kunde, Kunde, [land, plzColumn],
            [ForeignKey("FK_KUNDE_ORT", ["LAND", "PLZ"], new TableRef("SHOP", "ORT"), ["LAND", "PLZ"], enabled: enabled, validated: validated)]);

        Assert.Equal(warns, script.Contains("-- ⚠ Prüft die vorhandenen Zeilen und scheitert bei Verstößen.\nALTER TABLE \"SHOP\".\"KUNDE\" ADD CONSTRAINT \"FK_KUNDE_ORT\"", StringComparison.Ordinal));
        Assert.Contains("ADD CONSTRAINT \"FK_KUNDE_ORT\"", script);
    }

    /// <summary>
    /// A composite key keeps its own column order, not the schema order of the added columns, and its state: DEFERRABLE,
    /// DISABLE as in the source.
    /// </summary>
    [Fact]
    public void A_composite_foreign_key_keeps_its_column_order_and_its_state()
    {
        var script = AddColumnsScript.Write(Kunde, Kunde,
            [Column("POS_NR", "NUMBER", 2, precision: 4), Column("AUFTRAG_ID", "NUMBER", 3, precision: 10)],
            [ForeignKey("FK_KUNDE_POS", ["AUFTRAG_ID", "POS_NR"], new TableRef("SHOP", "POSITION"), ["AUFTRAG_ID", "NR"], enabled: false, deferrable: true, deferred: true)]);

        Assert.EndsWith(
            "ALTER TABLE \"SHOP\".\"KUNDE\" ADD CONSTRAINT \"FK_KUNDE_POS\" FOREIGN KEY (\"AUFTRAG_ID\", \"POS_NR\") REFERENCES \"SHOP\".\"POSITION\" (\"AUFTRAG_ID\", \"NR\") DEFERRABLE INITIALLY DEFERRED DISABLE VALIDATE;",
            script);
    }

    /// <summary>
    /// Review, finding 6: without privileges on the parent's owner the dictionary shows no referenced table – the key was
    /// dropped without a word. It is named as a note at the end, commented out.
    /// </summary>
    [Fact]
    public void A_foreign_key_whose_parent_cannot_be_read_is_named_as_a_note()
    {
        var script = AddColumnsScript.Write(Kunde, Kunde, [Column("LAND", "VARCHAR2", 7, length: 2)],
            [ForeignKey("FK_KUNDE_LAND", ["LAND"], null, [])]);

        Assert.EndsWith("\n\n-- Fremdschlüssel FK_KUNDE_LAND: Die Elterntabelle ist nicht lesbar (keine Rechte auf ihren Owner?) – er steht nicht im Skript.", script);
        Assert.DoesNotContain("ADD CONSTRAINT", script);
    }

    /// <summary>
    /// Review, finding 8 (the user's decision: a warning, no key): a key added together with a foreign key on it is not in
    /// the script – the foreign key fails with ORA-02270 unless the target has the key.
    /// </summary>
    [Fact]
    public void A_foreign_key_on_an_added_column_of_its_own_table_warns_that_the_key_is_missing()
    {
        var script = AddColumnsScript.Write(Kunde, Kunde,
            [Column("NR", "NUMBER", 2, precision: 10), Column("HAUPT_NR", "NUMBER", 3, precision: 10)],
            [ForeignKey("FK_KUNDE_HAUPT", ["HAUPT_NR"], Kunde, ["NR"])]);

        Assert.Contains("-- ⚠ Braucht im Ziel einen Primär- oder eindeutigen Schlüssel auf (NR) – das Skript legt ihn nicht an.\nALTER TABLE \"SHOP\".\"KUNDE\" ADD CONSTRAINT \"FK_KUNDE_HAUPT\"", script);
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
