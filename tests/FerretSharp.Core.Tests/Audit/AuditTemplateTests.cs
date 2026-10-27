using FerretSharp.Core.Audit;
using FerretSharp.Core.Oracle;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Tests.Audit;

/// <summary>The audit template of WP-26: parse, audit table, trigger.</summary>
public sealed class AuditTemplateTests
{
    /// <summary>The pattern of the user's company (neutral names): shared sequence, client identifier, I/U/D, NULL-safe comparison.</summary>
    internal const string CompanyTemplate = """
        -- @audit-table   AUDIT_{table}
        -- @trigger       {table}_AUDIT
        -- @table-comment Changes to {table}

        -- @columns
        AUDIT_ID    NUMBER(12)   NOT NULL  -- Running number (AUDIT_ID_SEQ)
        AUDIT_USER  VARCHAR2(30) NOT NULL  -- Client identifier, else database user
        AUDIT_TRANS CHAR(1)      NOT NULL  -- I = insert, U = update, D = delete
        AUDIT_TIME  DATE         NOT NULL  -- Time of the change
        AUDIT_SID   NUMBER(9)    NOT NULL  -- Session
        -- @base-columns as-base

        -- @trigger-source
        CREATE OR REPLACE TRIGGER {trigger}
        AFTER DELETE OR INSERT OR UPDATE ON {table}
        FOR EACH ROW
        DECLARE
          audit_trans CHAR(1);
        BEGIN
          IF DELETING THEN
            INSERT INTO {audit_table} (AUDIT_ID, AUDIT_USER, AUDIT_TRANS, AUDIT_TIME, AUDIT_SID{each}, {column}{end})
            VALUES (AUDIT_ID_SEQ.NEXTVAL, SUBSTR(NVL(SYS_CONTEXT('USERENV','CLIENT_IDENTIFIER'), USER), 1, 30),
                    'D', SYSDATE, USERENV('sessionid'){each}, :old.{column}{end});
          ELSE
            IF INSERTING THEN audit_trans := 'I';
            ELSIF {each comparable sep="\n       OR "}:new.{column} <> :old.{column} OR (:new.{column} IS NULL AND :old.{column} IS NOT NULL) OR (:new.{column} IS NOT NULL AND :old.{column} IS NULL){end}
            THEN audit_trans := 'U';
            ELSE RETURN;
            END IF;
            INSERT INTO {audit_table} (AUDIT_ID, AUDIT_USER, AUDIT_TRANS, AUDIT_TIME, AUDIT_SID{each}, {column}{end})
            VALUES (AUDIT_ID_SEQ.NEXTVAL, SUBSTR(NVL(SYS_CONTEXT('USERENV','CLIENT_IDENTIFIER'), USER), 1, 30),
                    audit_trans, SYSDATE, USERENV('sessionid'){each}, :new.{column}{end});
          END IF;
        END;
        """;

    private static readonly TableRef Kunde = new("APP", "KUNDE");

    /// <summary>A customer table with a LOB, a LONG, a mixed-case name and an identity key.</summary>
    internal static readonly IReadOnlyList<ColumnInfo> KundeColumns =
    [
        new("KUNDE_ID", "NUMBER", null, false, 10, 0, Nullable: false, IsIdentity: true, Default: "\"APP\".\"ISEQ$$_1\".nextval", Position: 1, Comment: "Customer number"),
        new("NAME", "VARCHAR2", 80, false, null, null, Nullable: true, IsIdentity: false, Default: null, Position: 2, Comment: "Name of the customer"),
        new("NOTIZ", "CLOB", null, false, null, null, Nullable: true, IsIdentity: false, Default: null, Position: 3),
        new("ALT", "LONG", null, false, null, null, Nullable: true, IsIdentity: false, Default: null, Position: 4),
        new("Bemerkung", "VARCHAR2", 200, true, null, null, Nullable: false, IsIdentity: false, Default: "'-'", Position: 5, Comment: "Remark"),
        new("NAME_UPPER", "VARCHAR2", 80, false, null, null, Nullable: true, IsIdentity: false, Default: "UPPER(\"NAME\")", Position: 6, IsVirtual: true),
    ];

    [Fact]
    public void Company_template_renders_the_trigger_exactly()
    {
        var trigger = AuditTemplates.Parse(CompanyTemplate).TriggerFor(Kunde, KundeColumns);

        const string expected = """
            CREATE OR REPLACE TRIGGER APP.KUNDE_AUDIT
            AFTER DELETE OR INSERT OR UPDATE ON APP.KUNDE
            FOR EACH ROW
            DECLARE
              audit_trans CHAR(1);
            BEGIN
              IF DELETING THEN
                INSERT INTO APP.AUDIT_KUNDE (AUDIT_ID, AUDIT_USER, AUDIT_TRANS, AUDIT_TIME, AUDIT_SID, KUNDE_ID, NAME, NOTIZ, "Bemerkung", NAME_UPPER)
                VALUES (AUDIT_ID_SEQ.NEXTVAL, SUBSTR(NVL(SYS_CONTEXT('USERENV','CLIENT_IDENTIFIER'), USER), 1, 30),
                        'D', SYSDATE, USERENV('sessionid'), :old.KUNDE_ID, :old.NAME, :old.NOTIZ, :old."Bemerkung", :old.NAME_UPPER);
              ELSE
                IF INSERTING THEN audit_trans := 'I';
                ELSIF :new.KUNDE_ID <> :old.KUNDE_ID OR (:new.KUNDE_ID IS NULL AND :old.KUNDE_ID IS NOT NULL) OR (:new.KUNDE_ID IS NOT NULL AND :old.KUNDE_ID IS NULL)
                   OR :new.NAME <> :old.NAME OR (:new.NAME IS NULL AND :old.NAME IS NOT NULL) OR (:new.NAME IS NOT NULL AND :old.NAME IS NULL)
                   OR :new."Bemerkung" <> :old."Bemerkung" OR (:new."Bemerkung" IS NULL AND :old."Bemerkung" IS NOT NULL) OR (:new."Bemerkung" IS NOT NULL AND :old."Bemerkung" IS NULL)
                   OR :new.NAME_UPPER <> :old.NAME_UPPER OR (:new.NAME_UPPER IS NULL AND :old.NAME_UPPER IS NOT NULL) OR (:new.NAME_UPPER IS NOT NULL AND :old.NAME_UPPER IS NULL)
                THEN audit_trans := 'U';
                ELSE RETURN;
                END IF;
                INSERT INTO APP.AUDIT_KUNDE (AUDIT_ID, AUDIT_USER, AUDIT_TRANS, AUDIT_TIME, AUDIT_SID, KUNDE_ID, NAME, NOTIZ, "Bemerkung", NAME_UPPER)
                VALUES (AUDIT_ID_SEQ.NEXTVAL, SUBSTR(NVL(SYS_CONTEXT('USERENV','CLIENT_IDENTIFIER'), USER), 1, 30),
                        audit_trans, SYSDATE, USERENV('sessionid'), :new.KUNDE_ID, :new.NAME, :new.NOTIZ, :new."Bemerkung", :new.NAME_UPPER);
              END IF;
            END;
            """;
        Assert.Equal(expected.ReplaceLineEndings("\n"), trigger.Sql);
        Assert.Equal("APP", trigger.Owner);
        Assert.Equal("KUNDE_AUDIT", trigger.Name);
        Assert.Equal(["KUNDE_ID", "NAME", "NOTIZ", "Bemerkung", "NAME_UPPER"], trigger.Columns);
        Assert.Empty(trigger.Problems);
        Assert.Equal(
        [
            "ALT (LONG) fehlt in der Audit-Tabelle und im Trigger: Ein Trigger hat für LONG kein :new/:old.",
            "Die Spaltenschleife mit comparable in Zeile 26 lässt NOTIZ aus: Diese Spalten lassen sich nicht mit <> vergleichen.",
        ], trigger.Warnings);
    }

    [Fact]
    public void Company_template_gives_the_audit_table_with_fixed_and_base_columns()
    {
        var shape = AuditTemplates.Parse(CompanyTemplate).TableFor(Kunde, KundeColumns);

        Assert.Equal(new TableRef("APP", "AUDIT_KUNDE"), shape.Table);
        Assert.Equal("Changes to KUNDE", shape.Comment);
        Assert.Empty(shape.Problems);
        Assert.Equal(["ALT (LONG) fehlt in der Audit-Tabelle und im Trigger: Ein Trigger hat für LONG kein :new/:old."], shape.Warnings);
        Assert.Equal(
        [
            "1 AUDIT_ID NUMBER(12) NOT NULL -- Running number (AUDIT_ID_SEQ)",
            "2 AUDIT_USER VARCHAR2(30 BYTE) NOT NULL -- Client identifier, else database user",
            "3 AUDIT_TRANS CHAR(1 BYTE) NOT NULL -- I = insert, U = update, D = delete",
            "4 AUDIT_TIME DATE NOT NULL -- Time of the change",
            "5 AUDIT_SID NUMBER(9) NOT NULL -- Session",
            "6 KUNDE_ID NUMBER(10) NOT NULL -- Customer number",
            "7 NAME VARCHAR2(80 BYTE) -- Name of the customer",
            "8 NOTIZ CLOB",
            "9 Bemerkung VARCHAR2(200 CHAR) NOT NULL -- Remark",
            "10 NAME_UPPER VARCHAR2(80 BYTE)",
        ], shape.Columns.Select(Describe));

        // Copies of base columns are plain columns: no identity, no default, not virtual (the trigger stores the values).
        Assert.All(shape.Columns, c => Assert.False(c.IsIdentity || c.IsVirtual || c.DefaultOnNull || c.Default is not null));
    }

    private static string Describe(ColumnInfo c) =>
        $"{c.Position} {c.Name} {OracleTypes.DdlType(c)}{(c.Nullable ? "" : " NOT NULL")}{(c.Comment is null ? "" : " -- " + c.Comment)}";

    [Fact]
    public void Nullable_base_columns_drop_not_null()
    {
        var template = AuditTemplates.Parse(CompanyTemplate.Replace("-- @base-columns as-base", "-- @base-columns nullable", StringComparison.Ordinal));

        var shape = template.TableFor(Kunde, KundeColumns);

        Assert.Equal(AuditBaseColumns.Nullable, template.BaseColumns);
        Assert.All(shape.Columns.Skip(5), c => Assert.True(c.Nullable));
        Assert.All(shape.Columns.Take(5), c => Assert.False(c.Nullable));
    }

    [Fact]
    public void Columns_after_the_base_columns_come_last()
    {
        var template = AuditTemplates.Parse(CompanyTemplate.Replace(
            "-- @base-columns as-base", "-- @base-columns as-base\nAUDIT_NOTE VARCHAR2(100 CHAR) DEFAULT 'none' -- Note", StringComparison.Ordinal));

        var columns = template.TableFor(Kunde, KundeColumns).Columns;

        Assert.Equal(["AUDIT_SID", "KUNDE_ID"], columns.Skip(4).Take(2).Select(c => c.Name));
        Assert.Equal(new ColumnInfo("AUDIT_NOTE", "VARCHAR2", 100, true, null, null, true, false, "'none'", 11, "Note"), columns[^1]);
        Assert.Single(template.ColumnsAfter);
    }

    [Fact]
    public void Trigger_passes_the_schema_path_guard_as_one_statement()
    {
        foreach (var template in new[] { AuditTemplates.Parse(CompanyTemplate), AuditTemplates.Default })
        {
            var trigger = template.TriggerFor(Kunde, KundeColumns);

            Assert.True(OracleSession.IsDdlStatement(trigger.Sql), trigger.Sql);
            var statement = Assert.Single(SqlScript.Split(trigger.Sql));
            Assert.EndsWith("END;", statement.Text.TrimEnd(), StringComparison.Ordinal);
            Assert.EndsWith("END;", trigger.Sql, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Default_template_parses_and_renders()
    {
        var template = AuditTemplates.Default;
        var shape = template.TableFor(Kunde, KundeColumns);
        var trigger = template.TriggerFor(Kunde, KundeColumns);

        Assert.Same(template, AuditTemplates.Default);
        Assert.Contains("-- @trigger-source", AuditTemplates.DefaultText, StringComparison.Ordinal);
        Assert.Equal(AuditBaseColumns.Nullable, template.BaseColumns);
        Assert.Equal(new TableRef("APP", "AUDIT_KUNDE"), shape.Table);
        Assert.Equal("Changes to KUNDE, written by trigger KUNDE_AUDIT", shape.Comment);
        Assert.Equal(["AUDIT_ID", "AUDIT_USER", "AUDIT_TRANS", "AUDIT_TIME", "AUDIT_SID", "KUNDE_ID", "NAME", "NOTIZ", "Bemerkung", "NAME_UPPER"],
            shape.Columns.Select(c => c.Name));
        Assert.True(shape.Columns[0].IsIdentity);
        Assert.False(shape.Columns[0].Nullable);
        Assert.All(shape.Columns.Take(5), c => Assert.NotNull(c.Comment));
        Assert.All(shape.Columns.Skip(5), c => Assert.True(c.Nullable));

        // The identity fills AUDIT_ID: the INSERTs leave it out.
        Assert.DoesNotContain("AUDIT_ID", trigger.Sql.Replace("AUDIT_ID_", "", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("INSERT INTO APP.AUDIT_KUNDE (AUDIT_USER, AUDIT_TRANS, AUDIT_TIME, AUDIT_SID, KUNDE_ID, NAME, NOTIZ, \"Bemerkung\", NAME_UPPER)", trigger.Sql, StringComparison.Ordinal);
        Assert.Contains(
            "ELSIF FALSE\n       OR (:new.KUNDE_ID <> :old.KUNDE_ID OR (:new.KUNDE_ID IS NULL AND :old.KUNDE_ID IS NOT NULL) OR (:new.KUNDE_ID IS NOT NULL AND :old.KUNDE_ID IS NULL))\n       OR (:new.NAME <> :old.NAME",
            trigger.Sql, StringComparison.Ordinal);

        // A LOB cannot be compared with <>: UPDATING tells it was set – no warning about skipped columns then.
        Assert.Contains("\n       OR UPDATING('NOTIZ')\n    THEN", trigger.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain(trigger.Warnings, w => w.Contains("NOTIZ", StringComparison.Ordinal));
        Assert.StartsWith("CREATE OR REPLACE TRIGGER APP.KUNDE_AUDIT\nAFTER DELETE OR INSERT OR UPDATE ON APP.KUNDE\n", trigger.Sql, StringComparison.Ordinal);
        Assert.Empty(trigger.Problems);
    }

    [Fact]
    public void Names_are_quoted_where_oracle_needs_it()
    {
        var table = new TableRef("Sales", "Order");
        ColumnInfo[] columns =
        [
            new("DATE", "DATE", null, false, null, null, true, false, null, 1),
            new("END", "NUMBER", null, false, null, null, true, false, null, 2),
            new("A B", "NUMBER", null, false, null, null, true, false, null, 3),
            new("ÄNDERUNG", "NUMBER", null, false, null, null, true, false, null, 4),
            new("PLAIN_1$#", "NUMBER", null, false, null, null, true, false, null, 5),
        ];
        var template = AuditTemplates.Parse("""
            -- @audit-table {table}_hist
            -- @trigger {table}_trg
            -- @base-columns nullable
            -- @trigger-source
            CREATE TRIGGER {trigger} AFTER INSERT ON {table} FOR EACH ROW
            BEGIN
              INSERT INTO {audit_table} ({each sep=", "}{column}{end}) VALUES ({each sep=", "}:new.{column}{end}); -- {owner}
            END;
            """);

        var names = template.NamesFor(table);
        var trigger = template.TriggerFor(table, columns);

        Assert.Equal(new TableRef("Sales", "Order_hist"), names.AuditTable);
        Assert.Equal("Order_trg", names.Trigger);
        Assert.Equal("""
            CREATE TRIGGER "Sales"."Order_trg" AFTER INSERT ON "Sales"."Order" FOR EACH ROW
            BEGIN
              INSERT INTO "Sales"."Order_hist" ("DATE", "END", "A B", "ÄNDERUNG", PLAIN_1$#) VALUES (:new."DATE", :new."END", :new."A B", :new."ÄNDERUNG", :new.PLAIN_1$#); -- "Sales"
            END;
            """.ReplaceLineEndings("\n"), trigger.Sql);
    }

    [Fact]
    public void Separators_take_escapes_and_without_sep_the_body_repeats()
    {
        var template = AuditTemplates.Parse("""
            -- @audit-table A_{table}
            -- @trigger T_{table}
            -- @base-columns nullable
            -- @trigger-source
            CREATE TRIGGER {trigger} AFTER INSERT ON {table} FOR EACH ROW
            BEGIN
              NULL; -- {each sep="\t|\"\\|\n"}{column}:{type}{end}
              NULL; -- {each}[{column}]{end} {{literal}} }}
            END;
            """);

        var sql = template.TriggerFor(Kunde, KundeColumns).Sql;

        Assert.Contains("NULL; -- KUNDE_ID:NUMBER(10)\t|\"\\|\nNAME:VARCHAR2(80 BYTE)\t|\"\\|\nNOTIZ:CLOB\t|\"\\|\n\"Bemerkung\":VARCHAR2(200 CHAR)\t|\"\\|\nNAME_UPPER:VARCHAR2(80 BYTE)\n", sql, StringComparison.Ordinal);
        Assert.Contains("NULL; -- [KUNDE_ID][NAME][NOTIZ][\"Bemerkung\"][NAME_UPPER] {literal} }\n", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Comparable_leaves_out_lobs_and_object_types()
    {
        var template = AuditTemplates.Parse("""
            -- @audit-table A_{table}
            -- @trigger T_{table}
            -- @base-columns nullable
            -- @trigger-source
            CREATE TRIGGER {trigger} AFTER UPDATE ON {table} FOR EACH ROW
            BEGIN
              NULL; -- {each comparable sep=","}:new.{column}{end}
            END;
            """);
        ColumnInfo C(string name, string type) => new(name, type, null, false, null, null, true, false, null, 0);
        ColumnInfo[] columns =
        [
            C("N", "NUMBER"), C("D", "DATE"), C("T", "TIMESTAMP(6) WITH TIME ZONE"), C("I", "INTERVAL DAY(2) TO SECOND(6)"),
            C("R", "RAW"), C("B", "BLOB"), C("C", "CLOB"), C("F", "BFILE"), C("X", "XMLTYPE"), C("J", "JSON"), C("O", "ADDRESS_T"),
            C("BF", "BINARY_FLOAT"), C("BO", "BOOLEAN"), C("NC", "NVARCHAR2"),
        ];

        var trigger = template.TriggerFor(Kunde, columns);

        Assert.Contains("NULL; -- :new.N,:new.D,:new.T,:new.I,:new.R,:new.BF,:new.BO,:new.NC\n", trigger.Sql, StringComparison.Ordinal);
        Assert.Equal(["N", "D", "T", "I", "R", "BF", "BO", "NC"], trigger.Columns);
        Assert.Equal(["Die Spaltenschleife mit comparable in Zeile 7 lässt B, C, F, X, J, O aus: Diese Spalten lassen sich nicht mit <> vergleichen."], trigger.Warnings);
    }

    [Fact]
    public void A_loop_without_columns_warns()
    {
        var template = AuditTemplates.Parse("""
            -- @audit-table A_{table}
            -- @trigger T_{table}
            -- @base-columns nullable
            -- @trigger-source
            CREATE TRIGGER {trigger} AFTER UPDATE ON {table} FOR EACH ROW
            BEGIN
              NULL; -- {each comparable}{column}{end}
            END;
            """);

        var trigger = template.TriggerFor(Kunde, [new("TEXT", "CLOB", null, false, null, null, true, false, null, 1)]);

        Assert.Contains("Die Spaltenschleife in Zeile 7 hat keine Spalten.", trigger.Warnings);
        Assert.Empty(trigger.Columns);
    }

    [Fact]
    public void Names_too_long_or_clashing_are_problems()
    {
        var longTable = new TableRef("APP", new string('K', 125));
        var template = AuditTemplates.Parse(CompanyTemplate);

        var names = template.NamesFor(longTable);
        var shape = template.TableFor(new TableRef("APP", "KUNDE"), [.. KundeColumns, new("AUDIT_TIME", "DATE", null, false, null, null, true, false, null, 7)]);
        var self = AuditTemplates.Parse(CompanyTemplate.Replace("AUDIT_{table}", "{table}", StringComparison.Ordinal)).NamesFor(Kunde);

        Assert.Equal(
        [
            $"AUDIT_{longTable.Name}: Der Name ist länger als 128 Byte.",
            $"{longTable.Name}_AUDIT: Der Name ist länger als 128 Byte.",
        ], names.Problems);
        Assert.Equal(names.Problems, template.TriggerFor(longTable, KundeColumns).Problems);
        Assert.Equal(["AUDIT_TIME ist eine Spalte der Vorlage und der Tabelle KUNDE."], shape.Problems);
        Assert.Equal(["Die Audit-Tabelle wäre die Tabelle KUNDE selbst."], self.Problems);
    }

    [Fact]
    public void Multibyte_names_count_bytes()
    {
        var template = AuditTemplates.Parse(CompanyTemplate);

        Assert.Empty(template.NamesFor(new TableRef("APP", new string('Ä', 61))).Problems); // AUDIT_ + 122 bytes = 128
        Assert.Equal(2, template.NamesFor(new TableRef("APP", new string('Ä', 62))).Problems.Count);
    }

    [Fact]
    public void Owner_placeholder_in_names_and_comment_with_trigger_name()
    {
        var template = AuditTemplates.Parse("""
            -- @audit-table   {owner}_{table}_A
            -- @trigger       TRG_{audit_table}
            -- @table-comment Audit of {owner}.{table} by {trigger} into {audit_table}
            -- @base-columns  as-base
            -- @trigger-source
            CREATE TRIGGER {trigger} AFTER INSERT ON {table} FOR EACH ROW BEGIN NULL; END;
            """);

        var shape = template.TableFor(Kunde, KundeColumns);

        var names = template.NamesFor(Kunde);
        Assert.Equal(new TableRef("APP", "APP_KUNDE_A"), names.AuditTable);
        Assert.Equal("TRG_APP_KUNDE_A", names.Trigger);
        Assert.Equal("Audit of APP.KUNDE by TRG_APP_KUNDE_A into APP_KUNDE_A", shape.Comment);
        Assert.Empty(template.ColumnsBefore);
        Assert.Equal("CREATE TRIGGER APP.TRG_APP_KUNDE_A AFTER INSERT ON APP.KUNDE FOR EACH ROW BEGIN NULL; END;", template.TriggerFor(Kunde, KundeColumns).Sql);
    }

    [Fact]
    public void Without_table_comment_the_comment_is_null_and_a_final_slash_is_dropped()
    {
        var template = AuditTemplates.Parse("""
            -- @audit-table A_{table}
            -- @trigger T_{table}
            -- @base-columns nullable
            -- @trigger-source

            CREATE TRIGGER {trigger} AFTER INSERT ON {table} FOR EACH ROW BEGIN NULL; END;

            /

            """.Replace("\n", "\r\n", StringComparison.Ordinal));

        Assert.Null(template.TableFor(Kunde, KundeColumns).Comment);
        Assert.Equal("CREATE TRIGGER APP.T_KUNDE AFTER INSERT ON APP.KUNDE FOR EACH ROW BEGIN NULL; END;", template.TriggerFor(Kunde, KundeColumns).Sql);
    }

    [Fact]
    public void Template_columns_take_identity_default_null_and_quoted_names()
    {
        var template = AuditTemplates.Parse("""
            -- @columns
            id1 number generated always as identity -- Always
            ID2 NUMBER(18) GENERATED BY DEFAULT ON NULL AS IDENTITY
            ID3 INTEGER GENERATED AS IDENTITY
            "Wann" TIMESTAMP(3) WITH LOCAL TIME ZONE DEFAULT ON NULL SYSTIMESTAMP
            WER VARCHAR2 ( 30 CHAR ) DEFAULT NVL(SYS_CONTEXT('USERENV', 'CLIENT_IDENTIFIER'), USER) NOT NULL -- Who -- with dashes
            WAS CHAR DEFAULT 'X' NULL
            WIE NUMBER(*,2) NULL
            -- a comment line, then a blank line

            WO FLOAT
            -- @base-columns as-base
            -- @audit-table A_{table}
            -- @trigger T_{table}
            -- @trigger-source
            CREATE TRIGGER {trigger} AFTER INSERT ON {table} FOR EACH ROW BEGIN NULL; END;
            """);

        Assert.Equal(
        [
            new("ID1", "NUMBER", null, false, null, null, false, true, null, 0, "Always"),
            new("ID2", "NUMBER", null, false, 18, 0, false, true, null, 0, DefaultOnNull: true),
            new("ID3", "NUMBER", null, false, null, 0, false, true, null, 0),
            new("Wann", "TIMESTAMP(3) WITH LOCAL TIME ZONE", null, false, null, 3, false, false, "SYSTIMESTAMP", 0, DefaultOnNull: true),
            new("WER", "VARCHAR2", 30, true, null, null, false, false, "NVL(SYS_CONTEXT('USERENV', 'CLIENT_IDENTIFIER'), USER)", 0, "Who -- with dashes"),
            new("WAS", "CHAR", 1, false, null, null, true, false, "'X'", 0),
            new("WIE", "NUMBER", null, false, null, 2, true, false, null, 0),
            new("WO", "FLOAT", null, false, 126, null, true, false, null, 0),
        ], template.ColumnsBefore);
    }

    [Theory]
    [InlineData("-- @audit-table A_{table}\n-- @trigger T_{table}\n-- @base-columns nullable\n", 0, "@trigger-source fehlt.")]
    [InlineData("-- @trigger T_{table}\n-- @base-columns nullable\n-- @trigger-source\nX", 0, "@audit-table fehlt.")]
    [InlineData("-- @audit-table A_{table}\n-- @base-columns nullable\n-- @trigger-source\nX", 0, "@trigger fehlt.")]
    [InlineData("-- @audit-table A_{table}\n-- @trigger T_{table}\n-- @trigger-source\nX", 0, "@base-columns fehlt.")]
    [InlineData("-- @audit-table A_{table}\n-- @trigger T_{table}\n-- @base-columns nullable\n-- @trigger-source\n\n/\n", 4, "@trigger-source ist leer.")]
    [InlineData("-- @audit-table\n-- @trigger T_{table}\n-- @base-columns nullable\n-- @trigger-source\nX", 1, "@audit-table braucht einen Wert.")]
    [InlineData("-- @audit-table A_{table}\n-- @audit-table B_{table}\n-- @trigger T_{table}\n-- @base-columns nullable\n-- @trigger-source\nX", 2, "@audit-table steht mehr als einmal in der Vorlage.")]
    [InlineData("-- @audit-table A_{table}\n-- @trigger T_{table}\n-- @base-columns all\n-- @trigger-source\nX", 3, "@base-columns nimmt „as-base“ oder „nullable“.")]
    [InlineData("-- @audit-table A_{table}\n-- @trigger T_{table}\n-- @base-columns nullable\n-- @trigger-source now\nX", 4, "@trigger-source nimmt keinen Wert.")]
    [InlineData("-- @audit-table A_{table}\n-- @trigger T_{table}\n-- @history yes\n-- @base-columns nullable\n-- @trigger-source\nX", 3, "Unbekannte Anweisung @history.")]
    [InlineData("-- @audit-table A_{table}\nSTRAY TEXT\n-- @trigger T_{table}\n-- @base-columns nullable\n-- @trigger-source\nX", 2, "Diese Zeile gehört zu keinem Abschnitt: Spalten stehen nach @columns, der Trigger nach @trigger-source.")]
    [InlineData("-- @audit-table A_{table}\n-- @trigger T_{table}\n-- @columns\nA NUMBER\nB VARCHAR2\n-- @base-columns nullable\n-- @trigger-source\nX", 5, "B: unbekannter Typ „VARCHAR2“.")]
    [InlineData("-- @audit-table A_{table}\n-- @trigger T_{table}\n-- @columns\nA NUMBER(39)\n-- @base-columns nullable\n-- @trigger-source\nX", 4, "A: unbekannter Typ „NUMBER(39)“.")]
    [InlineData("-- @audit-table A_{table}\n-- @trigger T_{table}\n-- @columns\nA ADDRESS_T\n-- @base-columns nullable\n-- @trigger-source\nX", 4, "A: unbekannter Typ „ADDRESS_T“.")]
    [InlineData("-- @audit-table A_{table}\n-- @trigger T_{table}\n-- @columns\nA NOT NULL\n-- @base-columns nullable\n-- @trigger-source\nX", 4, "A: Der Typ fehlt.")]
    [InlineData("-- @audit-table A_{table}\n-- @trigger T_{table}\n-- @columns\n(A) NUMBER\n-- @base-columns nullable\n-- @trigger-source\nX", 4, "Eine Spaltenzeile beginnt mit dem Spaltennamen.")]
    [InlineData("-- @audit-table A_{table}\n-- @trigger T_{table}\n-- @columns\nA NUMBER NOT NULL PRIMARY KEY\n-- @base-columns nullable\n-- @trigger-source\nX", 4, "A: „PRIMARY KEY“ nicht verstanden. Nach dem Typ folgen [GENERATED … AS IDENTITY | DEFAULT …] [NOT NULL | NULL] [-- Kommentar].")]
    [InlineData("-- @audit-table A_{table}\n-- @trigger T_{table}\n-- @columns\nA NUMBER GENERATED BY AS IDENTITY\n-- @base-columns nullable\n-- @trigger-source\nX", 4, "A: „AS IDENTITY“ nicht verstanden. Nach dem Typ folgen [GENERATED … AS IDENTITY | DEFAULT …] [NOT NULL | NULL] [-- Kommentar].")]
    [InlineData("-- @audit-table A_{table}\n-- @trigger T_{table}\n-- @columns\nA NUMBER DEFAULT :x\n-- @base-columns nullable\n-- @trigger-source\nX", 4, "A: Der Default fehlt oder passt nicht in ein Statement (kein „;“, keine Bind-Variable, passende Klammern).")]
    [InlineData("-- @audit-table A_{table}\n-- @trigger T_{table}\n-- @columns\nA NUMBER DEFAULT NOT NULL\n-- @base-columns nullable\n-- @trigger-source\nX", 4, "A: Der Default fehlt oder passt nicht in ein Statement (kein „;“, keine Bind-Variable, passende Klammern).")]
    [InlineData("-- @audit-table A_{table}\n-- @trigger T_{table}\n-- @columns\nA NUMBER\n-- @base-columns nullable\na DATE\n-- @trigger-source\nX", 6, "Die Spalte A steht mehr als einmal in der Vorlage.")]
    [InlineData("-- @audit-table A_{tabel}\n-- @trigger T_{table}\n-- @base-columns nullable\n-- @trigger-source\nX", 1, "Unbekannter Platzhalter {tabel}. Geschweifte Klammern als Text werden {{ und }} geschrieben.")]
    [InlineData("-- @audit-table A_{audit_table}\n-- @trigger T_{table}\n-- @base-columns nullable\n-- @trigger-source\nX", 1, "{audit_table} gibt es in @audit-table nicht.")]
    [InlineData("-- @audit-table A_{table}\n-- @trigger T_{trigger}\n-- @base-columns nullable\n-- @trigger-source\nX", 2, "{trigger} gibt es in @trigger nicht.")]
    [InlineData("-- @audit-table A_{table}\n-- @trigger T_{table}\n-- @table-comment {each}{column}{end}\n-- @base-columns nullable\n-- @trigger-source\nX", 3, "{each} gibt es in @table-comment nicht.")]
    [InlineData("-- @audit-table A_{table}\n-- @trigger T_{table}\n-- @base-columns nullable\n-- @trigger-source\nBEGIN\n  {column}\nEND;", 6, "{column} gibt es nur in einer Spaltenschleife ({each} … {end}).")]
    [InlineData("-- @audit-table A_{table}\n-- @trigger T_{table}\n-- @base-columns nullable\n-- @trigger-source\nBEGIN\n  {table x}\nEND;", 6, "{table} nimmt keine Optionen.")]
    [InlineData("-- @audit-table A_{table}\n-- @trigger T_{table}\n-- @base-columns nullable\n-- @trigger-source\nBEGIN\n  {each sorted}{column}{end}\nEND;", 6, "Unbekannte Option „sorted“: Eine Spaltenschleife nimmt comparable oder incomparable und sep=\"…\".")]
    [InlineData("-- @audit-table A_{table}\n-- @trigger T_{table}\n-- @base-columns nullable\n-- @trigger-source\nBEGIN\n  {each comparable comparable}{column}{end}\nEND;", 6, "Die Option comparable steht mehr als einmal da.")]
    [InlineData("-- @audit-table A_{table}\n-- @trigger T_{table}\n-- @base-columns nullable\n-- @trigger-source\nBEGIN\n  {each comparable incomparable}{column}{end}\nEND;", 6, "Eine Spaltenschleife nimmt entweder comparable oder incomparable.")]
    [InlineData("-- @audit-table A_{table}\n-- @trigger T_{table}\n-- @base-columns nullable\n-- @trigger-source\nBEGIN\n  UPDATING('{name}')\nEND;", 6, "{name} gibt es nur in einer Spaltenschleife ({each} … {end}).")]
    [InlineData("-- @audit-table A_{table}\n-- @trigger T_{table}\n-- @base-columns nullable\n-- @trigger-source\nBEGIN\n  {each sep=\"\\r\"}{column}{end}\nEND;", 6, "Unbekannte Escape-Sequenz „\\r“ in sep: möglich sind \\n, \\t, \\\" und \\\\.")]
    [InlineData("-- @audit-table A_{table}\n-- @trigger T_{table}\n-- @base-columns nullable\n-- @trigger-source\nBEGIN\n  {each sep=\", }{column}{end}\nEND;", 6, "Die Anführungszeichen von sep=\"…\" sind in dieser Zeile nicht geschlossen.")]
    [InlineData("-- @audit-table A_{table}\n-- @trigger T_{table}\n-- @base-columns nullable\n-- @trigger-source\nBEGIN\n  {each}{column}\n  {each}{column}{end}\nEND;", 7, "Spaltenschleifen lassen sich nicht schachteln: Vor diesem {each} fehlt {end}.")]
    [InlineData("-- @audit-table A_{table}\n-- @trigger T_{table}\n-- @base-columns nullable\n-- @trigger-source\nBEGIN\n\n  {each}{column}\nEND;", 7, "{each} ohne {end}.")]
    [InlineData("-- @audit-table A_{table}\n-- @trigger T_{table}\n-- @base-columns nullable\n-- @trigger-source\nBEGIN\n  {end}\nEND;", 6, "{end} ohne {each}.")]
    [InlineData("-- @audit-table A_{table}\n-- @trigger T_{table}\n-- @base-columns nullable\n-- @trigger-source\nBEGIN\n  x := '{2';\nEND;", 6, "„{“ ohne „}“ in dieser Zeile. Eine geschweifte Klammer als Text wird {{ geschrieben.")]
    [InlineData("-- @audit-table A_{table}\n-- @trigger T_{table}\n-- @base-columns nullable\n-- @trigger-source\nBEGIN\n  x := '2}';\nEND;", 6, "„}“ ohne „{“. Eine geschweifte Klammer als Text wird }} geschrieben.")]
    [InlineData("-- @audit-table A_{table}\n-- @trigger T_{table}\n-- @base-columns nullable\n-- @trigger-source\nBEGIN\n  IF REGEXP_LIKE(x, '^[0-9]{2}$') THEN NULL; END IF;\nEND;", 6, "Unbekannter Platzhalter {2}. Geschweifte Klammern als Text werden {{ und }} geschrieben.")]
    public void Template_errors_name_their_line(string text, int line, string message)
    {
        var error = Assert.Throws<AuditTemplateException>(() => AuditTemplates.Parse(text));

        Assert.Contains(new AuditTemplateError(line, message), error.Errors);
    }

    private const string WithColumns = "-- @audit-table A_{table}\n-- @trigger T_{table}\n-- @columns\nA_ID NUMBER\nA_TRANS CHAR(1)\nA_NOTE CLOB\n-- @base-columns nullable\n";

    [Fact]
    public void Version_order_and_action_name_own_columns()
    {
        var template = AuditTemplates.Parse(WithColumns + "-- @version-order a_id\n-- @action \"A_TRANS\" insert=I delete=D\n-- @trigger-source\nX");

        Assert.Equal("A_ID", template.VersionOrder);
        Assert.Equal(new AuditAction("A_TRANS", "I", null, "D"), template.Action);
        Assert.True(template.IsOwnColumn("A_TRANS"));
        Assert.False(template.IsOwnColumn("KUNDE_ID"));
    }

    [Fact]
    public void Without_the_directives_neither_is_set()
    {
        var template = AuditTemplates.Parse(CompanyTemplate);

        Assert.Null(template.VersionOrder);
        Assert.Null(template.Action);
    }

    [Fact]
    public void The_default_template_orders_by_its_id_and_tells_the_action()
    {
        Assert.Equal("AUDIT_ID", AuditTemplates.Default.VersionOrder);
        Assert.Equal(new AuditAction("AUDIT_TRANS", "I", "U", "D"), AuditTemplates.Default.Action);
    }

    [Theory]
    [InlineData("I ", AuditActionKind.Insert)] // CHAR(2) pads
    [InlineData("U", AuditActionKind.Update)]
    [InlineData("D", AuditActionKind.Delete)]
    [InlineData("X", null)]
    [InlineData(null, null)]
    public void The_action_of_a_value(string? value, AuditActionKind? kind) =>
        Assert.Equal(kind, new AuditAction("A", "I", "U", "D").KindOf(value));

    [Fact]
    public void Numbers_as_actions_compare_invariant() =>
        Assert.Equal(AuditActionKind.Delete, new AuditAction("A", "1", "2", "3").KindOf(3m));

    [Theory]
    [InlineData(WithColumns + "-- @version-order WANN\n-- @trigger-source\nX", 8, "@version-order nennt WANN, das ist keine eigene Spalte der Vorlage (nach @columns).")]
    [InlineData(WithColumns + "-- @version-order A_NOTE\n-- @trigger-source\nX", 8, "@version-order: Nach A_NOTE lassen sich die Versionen nicht ordnen (Typ CLOB).")]
    [InlineData(WithColumns + "-- @version-order A_ID DESC\n-- @trigger-source\nX", 8, "@version-order nimmt eine Spalte der Vorlage, nicht „A_ID DESC“.")]
    [InlineData(WithColumns + "-- @version-order\n-- @trigger-source\nX", 8, "@version-order braucht einen Wert.")]
    [InlineData(WithColumns + "-- @version-order A_ID\n-- @version-order A_ID\n-- @trigger-source\nX", 9, "@version-order steht mehr als einmal in der Vorlage.")]
    [InlineData(WithColumns + "-- @action A_TRANS\n-- @trigger-source\nX", 8, "@action braucht nach der Spalte mindestens eines von insert=…, update=…, delete=….")]
    [InlineData(WithColumns + "-- @action A_TRANS insert=I remove=D\n-- @trigger-source\nX", 8, "@action nimmt die Spalte und dann insert=…, update=…, delete=… – „remove=D“ ist nichts davon.")]
    [InlineData(WithColumns + "-- @action A_TRANS insert=\n-- @trigger-source\nX", 8, "@action nimmt die Spalte und dann insert=…, update=…, delete=… – „insert=“ ist nichts davon.")]
    [InlineData(WithColumns + "-- @action A_TRANS insert=I INSERT=N\n-- @trigger-source\nX", 8, "@action: insert= steht mehr als einmal da.")]
    [InlineData(WithColumns + "-- @action WAS insert=I\n-- @trigger-source\nX", 8, "@action nennt WAS, das ist keine eigene Spalte der Vorlage (nach @columns).")]
    [InlineData(WithColumns + "-- @action\n-- @trigger-source\nX", 8, "@action braucht einen Wert.")]
    public void Version_order_and_action_errors_name_their_line(string text, int line, string message)
    {
        var error = Assert.Throws<AuditTemplateException>(() => AuditTemplates.Parse(text));

        Assert.Contains(new AuditTemplateError(line, message), error.Errors);
    }

    [Fact]
    public void The_exception_lists_all_errors_with_lines()
    {
        var error = Assert.Throws<AuditTemplateException>(() => AuditTemplates.Parse("-- @audit-table A_{tabel}\n-- @base-columns nullable\n"));

        Assert.Equal("""
            Die Audit-Vorlage hat Fehler:
            Zeile 1: Unbekannter Platzhalter {tabel}. Geschweifte Klammern als Text werden {{ und }} geschrieben.
            @trigger fehlt.
            @trigger-source fehlt.
            """.ReplaceLineEndings("\n"), error.Message);
    }

    [Fact]
    public void Errors_are_english_in_the_english_ui()
    {
        using (UiCulture.Use("en"))
        {
            var error = Assert.Throws<AuditTemplateException>(() => AuditTemplates.Parse("-- @audit-table A_{table}\n-- @trigger T_{table}\n-- @base-columns nullable\n-- @trigger-source\n{column}"));

            Assert.Equal("Line 5: {column} only works inside a column loop ({each} … {end}).", error.Errors.Single().ToString());
        }
    }

    /// <summary>R5 review: the trigger source must create the trigger @trigger names and pass the schema path.</summary>
    [Fact]
    public void A_trigger_source_that_does_not_fit_is_a_problem()
    {
        var otherName = AuditTemplates.Parse(CompanyTemplate.Replace("CREATE OR REPLACE TRIGGER {trigger}", "CREATE OR REPLACE TRIGGER {table}_TRG", StringComparison.Ordinal));
        var system = AuditTemplates.Parse(CompanyTemplate.Replace("ON {table}", "ON DATABASE", StringComparison.Ordinal));

        Assert.Contains(otherName.TriggerFor(Kunde, KundeColumns).Problems, p => p.Contains("APP.KUNDE_TRG") && p.Contains("APP.KUNDE_AUDIT"));
        Assert.Contains(system.TriggerFor(Kunde, KundeColumns).Problems, p => p.Contains("ON DATABASE"));
        Assert.Empty(AuditTemplates.Parse(CompanyTemplate).TriggerFor(Kunde, KundeColumns).Problems);
    }

    /// <summary>
    /// R5 review: the trigger's columns are what ALL_TRIGGER_COLS lists – only loops that read :new/:old count, not a column
    /// list or UPDATING('{name}') alone.
    /// </summary>
    [Fact]
    public void Only_columns_read_as_new_or_old_count_as_the_triggers()
    {
        var template = AuditTemplates.Parse("""
            -- @audit-table A_{table}
            -- @trigger {table}_T
            -- @base-columns nullable
            -- @trigger-source
            CREATE OR REPLACE TRIGGER {trigger} AFTER UPDATE ON {table} FOR EACH ROW
            BEGIN
              IF FALSE{each incomparable} OR UPDATING('{name}'){end} THEN
                INSERT INTO {audit_table} ({each comparable sep=", "}{column}{end}) VALUES ({each comparable sep=", "}:new.{column}{end});
              END IF;
            END;
            """);

        var trigger = template.TriggerFor(Kunde, KundeColumns);

        Assert.DoesNotContain("NOTIZ", trigger.Columns); // only in UPDATING('NOTIZ')
        Assert.Contains("NAME", trigger.Columns);
    }
}
