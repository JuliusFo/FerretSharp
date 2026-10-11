using FerretSharp.Core.Compare;
using FerretSharp.Core.Design;
using FerretSharp.Core.Schema;
using static FerretSharp.Core.Tests.Compare.TestComparison;

namespace FerretSharp.Core.Tests.Ddl;

/// <summary>
/// The whole output of the DDL proposal and the designer for a few rich cases, pinned before R4b moved both onto one DDL
/// writer (<c>Core/Ddl</c>): what they write must stay what they wrote.
/// </summary>
public class DdlOutputTests
{
    private const string Ref = "DEV";
    private const string Tgt = "TEST";

    private static readonly ColumnInfo Id = Column("ID", "NUMBER", precision: 10, scale: 0, nullable: false);

    internal static DdlProposal Comparison()
    {
        var reference = new[]
        {
            Table("AUFTRAG",
                [Id, Column("KUNDE_ID", "NUMBER", precision: 10, scale: 0), Column("NR", "VARCHAR2", length: 20, comment: "Auftragsnummer"), Column("MENGE", "NUMBER")],
                [PrimaryKey("PK_AUFTRAG", "ID"), Check("SYS_C002", "MENGE > 0"), ForeignKey("FK_AUFTRAG_KUNDE", ["KUNDE_ID"], new TableRef(Ref, "KUNDE"), ["ID"], "CASCADE")],
                [UniqueIndex(Ref, "PK_AUFTRAG", On("ID")), Index(Ref, "IX_AUFTRAG_KUNDE", On("KUNDE_ID"))]),
            Table("KUNDE",
            [
                Id,
                Column("NAME", "VARCHAR2", length: 100, charSemantics: true, nullable: false),
                Column("BETRAG", "NUMBER", precision: 10, scale: 2),
                Column("TEXT", "VARCHAR2", length: 50),
                Column("STATUS", "CHAR", length: 1, @default: "'A'"),
                Column("LAND", "CHAR", length: 2, @default: "'DE'"),
                Column("eMail", "VARCHAR2", length: 200),
                Column("NOTIZ", "VARCHAR2", length: 500, comment: "neu"),
                Column("NEU", "NUMBER", nullable: false, comment: "Neue Spalte"),
                Column("BRUTTO", "NUMBER", isVirtual: true, @default: "BETRAG*2"),
            ],
            [PrimaryKey("PK_KUNDE", "ID"), Check("CK_KUNDE_STATUS", "STATUS IN ('A', 'B')"), Unique("UQ_KUNDE_NAME", "NAME")],
            [UniqueIndex(Ref, "PK_KUNDE", On("ID")), UniqueIndex(Ref, "UQ_KUNDE_NAME", On("NAME")), Index(Ref, "IX_KUNDE_LAND", On("LAND"), On("ID"))]),
            Table("V_KUNDE", [Id], kind: TableKind.View),
        };
        var target = new[]
        {
            Table("KUNDE",
            [
                Id,
                Column("NAME", "VARCHAR2", length: 50, charSemantics: true),
                Column("BETRAG", "NUMBER", precision: 10, scale: 0),
                Column("TEXT", "NVARCHAR2", length: 50),
                Column("STATUS", "CHAR", length: 1, @default: "'B'"),
                Column("LAND", "CHAR", length: 2, @default: "'DE'", defaultOnNull: true),
                Column("EMAIL", "VARCHAR2", length: 200),
                Column("NOTIZ", "VARCHAR2", length: 500, comment: "alt"),
                Column("ALT", "DATE"),
                Column("BRUTTO", "NUMBER", isVirtual: true, @default: "BETRAG*3"),
            ],
            [PrimaryKey("PK_KUNDE", "ID"), Check("CK_KUNDE_STATUS", "STATUS IN ('A')"), Check("CK_KUNDE_ALT", "ALT > DATE '2000-01-01'")],
            [UniqueIndex(Tgt, "PK_KUNDE", On("ID")), Index(Tgt, "IX_KUNDE_LAND", On("LAND")), Index(Tgt, "IX_KUNDE_ALT", On("ALT"))]),
            Table("ZOMBIE", [Id]),
        };
        return SchemaDdl.Align(Of(new CompareOptions(Reference: 0), Snapshot(Ref, reference), Snapshot(Tgt, target)), 0, 1);
    }

    private static readonly ColumnInfo DId = new("ID", "NUMBER", null, false, 10, 0, false, false, null, 1);
    private static readonly ColumnInfo DName = new("NAME", "VARCHAR2", 50, true, null, null, true, false, null, 2);
    private static readonly ColumnInfo DStatus = new("STATUS", "CHAR", 1, false, null, null, false, false, "'A' ", 3, "Status");
    private static readonly ColumnInfo DUmsatz = new("UMSATZ", "NUMBER", null, false, 12, 2, true, false, null, 4);
    private static readonly ColumnInfo DNotiz = new("NOTIZ", "VARCHAR2", 200, false, null, null, true, false, null, 5);
    private static readonly ColumnInfo DAlt = new("ALT", "DATE", null, false, null, null, true, false, null, 6);
    private static readonly ColumnInfo DLand = new("LAND", "CHAR", 2, false, null, null, true, false, "'DE'", 7, DefaultOnNull: true);

    private static readonly TableDetails Kunde = new(new TableSummary("SHOP", "KUNDE", TableKind.Table), [DId, DName, DStatus, DUmsatz, DNotiz, DAlt, DLand], ["ID"], [], false);

    private static readonly TableDetails Land = new(new TableSummary("SHOP", "LAND", TableKind.Table),
        [new ColumnInfo("CODE", "VARCHAR2", 2, false, null, null, false, false, null, 1)], ["CODE"], [], false);

    internal static DesignPlan Designer()
    {
        var unique = new ConstraintInfo("UK_KUNDE", ConstraintType.Unique, ["NAME", "ALT"], null, null, [], null, true, true, false, false, false);
        var pk = new ConstraintInfo("PK_KUNDE", ConstraintType.PrimaryKey, ["ID"], null, null, [], null, true, true, false, false, false);
        var draft = new TableDraft(
        [
            new ColumnDraft("ID", DId),
            new ColumnDraft("NAME", DName with { Name = "NOTIZ", Length = 80, Nullable = false, Comment = "Name" }),
            new ColumnDraft("STATUS", DStatus with { Default = "'B'", Comment = null }),
            new ColumnDraft("UMSATZ", DUmsatz with { Precision = 14, Scale = 3 }),
            new ColumnDraft("NOTIZ", DNotiz with { Name = "NAME", Length = 100 }),
            new ColumnDraft("ALT", DAlt, Dropped: true),
            new ColumnDraft("LAND", DLand with { DefaultOnNull = false, Nullable = false, Default = "'AT'" }),
            new ColumnDraft(null, new ColumnInfo("FAX", "VARCHAR2", 30, true, null, null, true, false, null, 0, "Fax-Nummer")),
            new ColumnDraft(null, new ColumnInfo("LAND_CODE", "VARCHAR2", 2, false, null, null, true, false, null, 0)),
        ])
        {
            DroppedConstraints = ["UK_KUNDE"],
            AddedConstraints =
            [
                new ConstraintDraft("FK_KUNDE_LAND", ConstraintType.ForeignKey, ["LAND_CODE"], References: Land.Table.Ref, ReferencedColumns: ["CODE"]),
                new ConstraintDraft("CK_KUNDE_UMSATZ", ConstraintType.Check, [], "UMSATZ >= 0"),
                new ConstraintDraft("UK_KUNDE_FAX", ConstraintType.Unique, ["FAX"]),
            ],
        };
        var context = new DesignContext(Kunde, [pk, unique], [], []) { Referenced = new Dictionary<TableRef, TableDetails> { [Land.Table.Ref] = Land } };
        var facts = new DataFacts(new Dictionary<DataQuestion, long>
        {
            [new DataQuestion(DataQuestionKind.Rows)] = 5,
            [new DataQuestion(DataQuestionKind.Nulls, "NAME")] = 0,
        });
        return TableDesign.Plan(context, draft, facts);
    }

    internal static DesignPlan NewTable()
    {
        var draft = new TableDraft(
        [
            new ColumnDraft(null, new ColumnInfo("ID", "NUMBER", null, false, 10, 0, false, true, null, 0)),
            new ColumnDraft(null, new ColumnInfo("PLZ", "VARCHAR2", 5, false, null, null, false, false, null, 0, "Postleitzahl")),
            new ColumnDraft(null, new ColumnInfo("LAND", "VARCHAR2", 2, false, null, null, true, false, "'DE'", 0)),
            new ColumnDraft(null, new ColumnInfo("PARENT_ID", "NUMBER", null, false, 10, 0, true, false, null, 0)),
        ])
        {
            AddedConstraints =
            [
                new ConstraintDraft("FK_ORT_PARENT", ConstraintType.ForeignKey, ["PARENT_ID"], References: new TableRef("SHOP", "ORT"), ReferencedColumns: ["ID"]),
                new ConstraintDraft("FK_ORT_LAND", ConstraintType.ForeignKey, ["LAND"], References: Land.Table.Ref, ReferencedColumns: ["CODE"], DeleteRule: "SET NULL"),
                new ConstraintDraft("PK_ORT", ConstraintType.PrimaryKey, ["ID"]),
                new ConstraintDraft("", ConstraintType.Check, [], "PLZ <> '00000'"),
            ],
        };
        return TableDesign.PlanNew(new NewTable("SHOP", "ORT", "Orte"), draft, new Dictionary<TableRef, TableDetails> { [Land.Table.Ref] = Land }, ["KUNDE"]);
    }

    [Fact]
    public void The_comparison_writes_its_script_as_before()
    {
        Assert.Equal(
            """
            -- DDL-Vorschlag: TEST (Seite 2) an die Referenz DEV (Seite 1) angleichen.
            -- Von FerretSharp erzeugt, nicht ausgeführt: vor dem Ausführen prüfen. Auskommentiertes bewusst entscheiden.

            CREATE TABLE "TEST"."AUFTRAG" (
                "ID" NUMBER(10) NOT NULL,
                "KUNDE_ID" NUMBER(10),
                "NR" VARCHAR2(20 BYTE),
                "MENGE" NUMBER);

            COMMENT ON COLUMN "TEST"."AUFTRAG"."NR" IS 'Auftragsnummer';

            ALTER TABLE "TEST"."KUNDE" MODIFY ("NAME" VARCHAR2(100 CHAR));

            -- ⚠ Schlägt fehl, wenn die Tabelle Zeilen mit NULL in dieser Spalte hat.
            ALTER TABLE "TEST"."KUNDE" MODIFY ("NAME" NOT NULL);

            -- ⚠ Kann an vorhandenen Daten scheitern (Länge, Genauigkeit oder BYTE/CHAR).
            ALTER TABLE "TEST"."KUNDE" MODIFY ("BETRAG" NUMBER(10,2));

            -- ⚠ Kann an vorhandenen Daten scheitern (Länge, Genauigkeit oder BYTE/CHAR).
            ALTER TABLE "TEST"."KUNDE" MODIFY ("TEXT" VARCHAR2(50 BYTE));

            ALTER TABLE "TEST"."KUNDE" MODIFY ("STATUS" DEFAULT 'A');

            ALTER TABLE "TEST"."KUNDE" MODIFY ("LAND" DEFAULT 'DE');

            -- ⚠ Nur nötig, wenn die Spalte nach dem Entfernen von DEFAULT ON NULL noch NOT NULL ist (Oracle 23 hebt beides zusammen auf).
            -- ALTER TABLE "TEST"."KUNDE" MODIFY ("LAND" NULL)

            COMMENT ON COLUMN "TEST"."KUNDE"."NOTIZ" IS 'neu';

            -- ⚠ Schlägt fehl, wenn die Tabelle Zeilen hat (NOT NULL ohne Default).
            ALTER TABLE "TEST"."KUNDE" ADD ("NEU" NUMBER NOT NULL);

            COMMENT ON COLUMN "TEST"."KUNDE"."NEU" IS 'Neue Spalte';

            ALTER TABLE "TEST"."AUFTRAG" ADD CONSTRAINT "PK_AUFTRAG" PRIMARY KEY ("ID");

            -- ⚠ Prüft die vorhandenen Zeilen und scheitert bei Verstößen.
            ALTER TABLE "TEST"."KUNDE" ADD CONSTRAINT "UQ_KUNDE_NAME" UNIQUE ("NAME");

            ALTER TABLE "TEST"."AUFTRAG" ADD CHECK (MENGE > 0);

            -- ⚠ Wird gelöscht und neu angelegt; das Anlegen prüft die vorhandenen Zeilen.
            ALTER TABLE "TEST"."KUNDE" DROP CONSTRAINT "CK_KUNDE_STATUS";

            ALTER TABLE "TEST"."KUNDE" ADD CONSTRAINT "CK_KUNDE_STATUS" CHECK (STATUS IN ('A', 'B'));

            ALTER TABLE "TEST"."AUFTRAG" ADD CONSTRAINT "FK_AUFTRAG_KUNDE" FOREIGN KEY ("KUNDE_ID") REFERENCES "TEST"."KUNDE" ("ID") ON DELETE CASCADE;

            CREATE INDEX "TEST"."IX_AUFTRAG_KUNDE" ON "TEST"."AUFTRAG" ("KUNDE_ID");

            -- ⚠ Wird gelöscht und neu aufgebaut. Stützt er einen Schlüssel, scheitert das Löschen (ORA-02429).
            DROP INDEX "TEST"."IX_KUNDE_LAND";

            CREATE INDEX "TEST"."IX_KUNDE_LAND" ON "TEST"."KUNDE" ("LAND", "ID");

            -- ⚠ Spalte nur in anderer Schreibweise. Code und EF Core (quotet Namen) verweisen auf den genauen Namen – erst klären, welcher richtig ist.
            -- ALTER TABLE "TEST"."KUNDE" RENAME COLUMN "EMAIL" TO "eMail"

            -- ⚠ Identity- und virtuelle Spalten lassen sich nicht per MODIFY umstellen: neue Spalte anlegen und Daten umkopieren.
            -- "TEST"."KUNDE"."BRUTTO": Referenz virtuell AS (BETRAG*2), Ziel virtuell AS (BETRAG*3)

            -- ⚠ Löscht die Spalte mit ihren Daten.
            -- ALTER TABLE "TEST"."KUNDE" DROP COLUMN "ALT"

            -- ⚠ Löscht die Prüfung.
            -- ALTER TABLE "TEST"."KUNDE" DROP CONSTRAINT "CK_KUNDE_ALT"

            -- ⚠ Löscht den Index. Stützt er einen Schlüssel, lehnt Oracle das ab (ORA-02429).
            -- DROP INDEX "TEST"."IX_KUNDE_ALT"

            -- ⚠ Die Definition der View gehört nicht zum Vergleich: aus der DDL-Ansicht der Referenz übernehmen.
            -- CREATE VIEW "TEST"."V_KUNDE" AS …

            -- ⚠ Löscht die Tabelle mit allen Daten.
            -- DROP TABLE "TEST"."ZOMBIE"

            """.ReplaceLineEndings("\n"),
            Comparison().Script.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void The_designer_writes_its_statements_as_before()
    {
        var plan = Designer();

        Assert.Equal(
        [
            """ALTER TABLE "SHOP"."KUNDE" DROP CONSTRAINT "UK_KUNDE" """.TrimEnd(),
            """ALTER TABLE "SHOP"."KUNDE" DROP COLUMN "ALT" """.TrimEnd(),
            """ALTER TABLE "SHOP"."KUNDE" RENAME COLUMN "NAME" TO "FS_RENAME_1" """.TrimEnd(),
            """ALTER TABLE "SHOP"."KUNDE" RENAME COLUMN "NOTIZ" TO "NAME" """.TrimEnd(),
            """ALTER TABLE "SHOP"."KUNDE" RENAME COLUMN "FS_RENAME_1" TO "NOTIZ" """.TrimEnd(),
            """ALTER TABLE "SHOP"."KUNDE" MODIFY ("NOTIZ" VARCHAR2(80 CHAR))""",
            """ALTER TABLE "SHOP"."KUNDE" MODIFY ("NOTIZ" NOT NULL)""",
            """ALTER TABLE "SHOP"."KUNDE" MODIFY ("STATUS" DEFAULT 'B')""",
            """ALTER TABLE "SHOP"."KUNDE" MODIFY ("UMSATZ" NUMBER(14,3))""",
            """ALTER TABLE "SHOP"."KUNDE" MODIFY ("NAME" VARCHAR2(100 BYTE))""",
            """ALTER TABLE "SHOP"."KUNDE" MODIFY ("LAND" DEFAULT 'AT')""",
            """ALTER TABLE "SHOP"."KUNDE" MODIFY ("LAND" NOT NULL)""",
            """ALTER TABLE "SHOP"."KUNDE" ADD ("FAX" VARCHAR2(30 CHAR))""",
            """ALTER TABLE "SHOP"."KUNDE" ADD ("LAND_CODE" VARCHAR2(2 BYTE))""",
            """ALTER TABLE "SHOP"."KUNDE" ADD CONSTRAINT "UK_KUNDE_FAX" UNIQUE ("FAX")""",
            """ALTER TABLE "SHOP"."KUNDE" ADD CONSTRAINT "CK_KUNDE_UMSATZ" CHECK (UMSATZ >= 0)""",
            """ALTER TABLE "SHOP"."KUNDE" ADD CONSTRAINT "FK_KUNDE_LAND" FOREIGN KEY ("LAND_CODE") REFERENCES "SHOP"."LAND" ("CODE")""",
            """CREATE INDEX "SHOP"."IX_KUNDE_LAND_CODE" ON "SHOP"."KUNDE" ("LAND_CODE")""",
            """COMMENT ON COLUMN "SHOP"."KUNDE"."NOTIZ" IS 'Name'""",
            """COMMENT ON COLUMN "SHOP"."KUNDE"."STATUS" IS ''""",
            """COMMENT ON COLUMN "SHOP"."KUNDE"."FAX" IS 'Fax-Nummer'""",
        ], plan.Statements);
        Assert.Empty(plan.Problems);
        Assert.Equal(
        [
            "NAME → NOTIZ: Views, PL/SQL und das C#-Modell (HasColumnName) verwenden noch den alten Namen.",
            "NOTIZ → NAME: Views, PL/SQL und das C#-Modell (HasColumnName) verwenden noch den alten Namen.",
            "NAME: VARCHAR2(200) → VARCHAR2(100) scheitert, wenn ein Wert zu lang dafür ist (ORA-01441).",
            "ALT wird mit allen Werten gelöscht – ohne Rückweg.",
            "Neue Constraints prüfen die vorhandenen Zeilen: Duplikate, NULLs, Zeilen gegen einen Check oder ohne Elternzeile lassen sie scheitern (ORA-02437, ORA-02299, ORA-02293, ORA-02298).",
        ], plan.Warnings);
    }

    [Fact]
    public void The_designer_writes_a_new_table_as_before()
    {
        var plan = NewTable();

        Assert.Equal(
        [
            """
            CREATE TABLE "SHOP"."ORT" (
                "ID" NUMBER(10) GENERATED BY DEFAULT AS IDENTITY,
                "PLZ" VARCHAR2(5 BYTE) NOT NULL,
                "LAND" VARCHAR2(2 BYTE) DEFAULT 'DE',
                "PARENT_ID" NUMBER(10))
            """.ReplaceLineEndings("\n"),
            """ALTER TABLE "SHOP"."ORT" ADD CONSTRAINT "PK_ORT" PRIMARY KEY ("ID")""",
            """ALTER TABLE "SHOP"."ORT" ADD CHECK (PLZ <> '00000')""",
            """ALTER TABLE "SHOP"."ORT" ADD CONSTRAINT "FK_ORT_PARENT" FOREIGN KEY ("PARENT_ID") REFERENCES "SHOP"."ORT" ("ID")""",
            """ALTER TABLE "SHOP"."ORT" ADD CONSTRAINT "FK_ORT_LAND" FOREIGN KEY ("LAND") REFERENCES "SHOP"."LAND" ("CODE") ON DELETE SET NULL""",
            """CREATE INDEX "SHOP"."IX_ORT_PARENT_ID" ON "SHOP"."ORT" ("PARENT_ID")""",
            """CREATE INDEX "SHOP"."IX_ORT_LAND" ON "SHOP"."ORT" ("LAND")""",
            """COMMENT ON TABLE "SHOP"."ORT" IS 'Orte'""",
            """COMMENT ON COLUMN "SHOP"."ORT"."PLZ" IS 'Postleitzahl'""",
        ], plan.Statements);
        Assert.Empty(plan.Problems);
        Assert.Empty(plan.Warnings);
    }
}
