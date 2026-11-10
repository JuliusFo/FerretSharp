using FerretSharp.Core.Query;

namespace FerretSharp.Core.Tests.Query;

public sealed class SqlScriptTests
{
    private static List<(SqlTokenKind, string)> Tokens(string text) =>
        SqlScript.Tokenize(text).Where(t => t.Kind != SqlTokenKind.Space).Select(t => (t.Kind, t.Value)).ToList();

    [Fact]
    public void Literals_comments_quoted_names_and_binds_are_single_tokens()
    {
        Assert.Equal(
            [
                (SqlTokenKind.Word, "SELECT"), (SqlTokenKind.Text, "'a;b''c'"), (SqlTokenKind.Symbol, ","), (SqlTokenKind.Text, "q'[it's; ok]'"),
                (SqlTokenKind.Symbol, ","), (SqlTokenKind.Text, "N'Grüße'"), (SqlTokenKind.Symbol, ","), (SqlTokenKind.QuotedIdentifier, "Mixed;Case"),
                (SqlTokenKind.Comment, "-- :nobind;"), (SqlTokenKind.Word, "FROM"), (SqlTokenKind.Word, "t"), (SqlTokenKind.Comment, "/* ; */"),
                (SqlTokenKind.Word, "WHERE"), (SqlTokenKind.Word, "a"), (SqlTokenKind.Symbol, "<="), (SqlTokenKind.Bind, "kundeId"),
                (SqlTokenKind.Word, "AND"), (SqlTokenKind.Word, "b"), (SqlTokenKind.Symbol, "<>"), (SqlTokenKind.Bind, "1"),
                (SqlTokenKind.Word, "AND"), (SqlTokenKind.Word, "c"), (SqlTokenKind.Symbol, "="), (SqlTokenKind.Bind, "Quoted Name"),
                (SqlTokenKind.Word, "AND"), (SqlTokenKind.Word, "d"), (SqlTokenKind.Symbol, "="), (SqlTokenKind.Number, "1.5e-3"),
            ],
            Tokens("SELECT 'a;b''c', q'[it's; ok]', N'Grüße', \"Mixed;Case\" -- :nobind;\nFROM t /* ; */ WHERE a <= :kundeId AND b <> :1 AND c = :\"Quoted Name\" AND d = 1.5e-3"));
    }

    [Fact]
    public void Assignment_is_no_bind_and_words_ending_in_n_or_q_are_no_literal_prefix()
    {
        Assert.Equal([(SqlTokenKind.Word, "x"), (SqlTokenKind.Symbol, ":="), (SqlTokenKind.Number, "1")], Tokens("x := 1"));
        Assert.Equal([(SqlTokenKind.Word, "WHEN"), (SqlTokenKind.Text, "'a'")], Tokens("WHEN'a'"));
        Assert.Equal([(SqlTokenKind.Text, "nq'{x}'")], Tokens("nq'{x}'"));
    }

    [Fact]
    public void Statements_end_at_semicolons_and_slash_lines_not_at_blank_lines_or_inside_literals_or_comments()
    {
        const string script = """
            SELECT 'a;b' FROM dual; SELECT 2 FROM dual;
            -- the next one
            UPDATE t
               SET x = 1 /* stays

            in here */
            /
            DELETE FROM t

            -- still the DELETE

             WHERE id = 1;

            -- only a comment
            """;

        Assert.Equal(
            [
                "SELECT 'a;b' FROM dual",
                "SELECT 2 FROM dual",
                "-- the next one\nUPDATE t\n   SET x = 1 /* stays\n\nin here */",
                "DELETE FROM t\n\n-- still the DELETE\n\n WHERE id = 1",
            ],
            SqlScript.Split(script.ReplaceLineEndings("\n")).Select(s => s.Text));
    }

    /// <summary>
    /// ADR 0014, decision of the user: a blank line separated statements, so a view laid out with blank lines was cut
    /// apart – Ctrl+Enter in its lower part ran "PIVOT …" and was refused.
    /// </summary>
    [Fact]
    public void A_view_with_blank_lines_is_one_statement()
    {
        const string script = """
            CREATE OR REPLACE FORCE EDITIONABLE VIEW messwerte_v AS
            SELECT *
              FROM (
                SELECT m.probe_id, m.name, m.wert
                  FROM messung m

                 WHERE m.typ = 1
              )

            PIVOT (MAX(wert) FOR name IN ('Q1' AS q1, 'Q2' AS q2));

            SELECT * FROM messwerte_v
            """;
        var text = script.ReplaceLineEndings("\n");

        var statement = SqlScript.StatementAt(text, text.IndexOf("PIVOT", StringComparison.Ordinal), text.IndexOf("PIVOT", StringComparison.Ordinal))!;

        Assert.Equal(text[..text.IndexOf(';', StringComparison.Ordinal)], statement.Text);
        Assert.Equal(SqlStatementKind.Ddl, SqlScript.Analyze(statement.Text).Kind);
        Assert.Null(SqlScript.DdlRejection(statement.Text));
        Assert.Equal(2, SqlScript.Split(text).Count);
    }

    /// <summary>The history appends after the script's last statement: without its own ";" or "/" line the two would run as one.</summary>
    [Theory]
    [InlineData("SELECT 1 FROM dual", "SELECT 1 FROM dual;")]
    [InlineData("SELECT 1 FROM dual -- note", "SELECT 1 FROM dual; -- note")]
    [InlineData("SELECT 1 FROM dual;\nSELECT 2 FROM dual", "SELECT 1 FROM dual;\nSELECT 2 FROM dual;")]
    [InlineData("BEGIN NULL; END;", "BEGIN NULL; END;\n/")]
    [InlineData("SELECT 1 FROM dual; -- done", "SELECT 1 FROM dual; -- done")]
    [InlineData("BEGIN NULL; END;\n/", "BEGIN NULL; END;\n/")]
    [InlineData("SELECT 1 FROM dual\n/", "SELECT 1 FROM dual\n/")]
    [InlineData("-- only a comment", "-- only a comment")]
    [InlineData("", "")]
    [InlineData("SELECT 1 FROM dual\r\n-- note", "SELECT 1 FROM dual;\r\n-- note")]
    [InlineData("SELECT 1 FROM dual /* note */", "SELECT 1 FROM dual; /* note */")]
    [InlineData("SELECT 10\n/ 2 FROM dual", "SELECT 10\n/ 2 FROM dual;")]
    [InlineData("BEGIN NULL; END; -- note", "BEGIN NULL; END; -- note\n/")]
    [InlineData("CREATE TRIGGER t_bi BEFORE INSERT ON t FOR EACH ROW CALL p", "CREATE TRIGGER t_bi BEFORE INSERT ON t FOR EACH ROW CALL p\n/")]
    [InlineData("UPDATE t SET a = 1\nBEGIN NULL; END;", "UPDATE t SET a = 1\nBEGIN NULL; END;\n/")]
    public void Terminated_ends_an_open_last_statement(string text, string expected) => Assert.Equal(expected, SqlScript.Terminated(text));

    /// <summary>
    /// Review: the history ended only the script's last statement; one of its own ending in a comment or a block stayed
    /// open. Second review: line breaks of the script's kind – Monaco turns others into its own, and the cursor missed.
    /// </summary>
    [Theory]
    [InlineData("SELECT 1 FROM dual\n\n", "SELECT 2 FROM dual -- note", "SELECT 1 FROM dual;\n\nSELECT 2 FROM dual; -- note\n", 21)]
    [InlineData("SELECT 1 FROM dual;\n", "BEGIN NULL; END;", "SELECT 1 FROM dual;\n\nBEGIN NULL; END;\n/\n", 21)]
    [InlineData("SELECT 1 FROM dual\r\n", "SELECT 2 FROM dual", "SELECT 1 FROM dual;\r\n\r\nSELECT 2 FROM dual;\r\n", 23)]
    [InlineData("SELECT 1\r\nFROM dual", "BEGIN NULL; END;", "SELECT 1\r\nFROM dual;\r\n\r\nBEGIN NULL; END;\r\n/\r\n", 24)]
    public void The_history_appends_a_statement_of_its_own(string script, string statement, string expected, int start)
    {
        var appended = SqlScript.Append(script, statement);

        Assert.Equal((expected, start), appended);
        Assert.Equal(2, SqlScript.Split(appended.Text + "SELECT 3 FROM dual").Count - 1);
        Assert.Equal((SqlScript.Terminated("SELECT 1 FROM dual") + Environment.NewLine, 0), SqlScript.Append("", "SELECT 1 FROM dual"));
    }

    /// <summary>
    /// WP-77: a script from the library goes in at the cursor or in place of the selection – the statement before it ended,
    /// the text after it on a line of its own.
    /// </summary>
    [Theory]
    // at the end
    [InlineData("SELECT 1 FROM dual\n", 19, 19, "SELECT 2 FROM dual", "SELECT 1 FROM dual;\n\nSELECT 2 FROM dual;\n", 21)]
    // between two statements
    [InlineData("SELECT 1 FROM dual;\nSELECT 3 FROM dual;", 20, 20, "SELECT 2 FROM dual", "SELECT 1 FROM dual;\n\nSELECT 2 FROM dual;\n\nSELECT 3 FROM dual;", 21)]
    // in place of the selection
    [InlineData("SELECT 1 FROM dual;\nDELETE FROM t;\n", 20, 34, "SELECT 2 FROM dual", "SELECT 1 FROM dual;\n\nSELECT 2 FROM dual;\n", 21)]
    // a block
    [InlineData("SELECT 1 FROM dual\n", 19, 19, "BEGIN NULL; END;", "SELECT 1 FROM dual;\n\nBEGIN NULL; END;\n/\n", 21)]
    public void A_script_is_inserted_at_the_cursor_or_in_place_of_the_selection(string script, int start, int end, string statement, string expected, int at)
    {
        Assert.Equal((expected, at), SqlScript.Insert(script, start, end, statement));
        Assert.Equal(("SELECT 2 FROM dual;" + Environment.NewLine, 0), SqlScript.Insert("", 0, 0, "SELECT 2 FROM dual")); // an empty tab
    }

    /// <summary>
    /// WP-81: a result's statement is found again in the editor only with exactly its text – where it was if it still is
    /// there (the same text twice: not the first one), else wherever it went; changed or gone, it is not found.
    /// </summary>
    [Theory]
    [InlineData("SELECT 1 FROM dual;\nSELECT 2 FROM dual;", 20, 20)] // where it was
    [InlineData("SELECT 2 FROM dual;\nSELECT 1 FROM dual;", 20, 0)] // moved
    [InlineData("SELECT 2 FROM dual;\nSELECT 2 FROM dual;", 20, 20)] // twice: the one where it was
    [InlineData("SELECT 2 FROM dual;\n\nSELECT 2 FROM dual;", 20, 0)] // twice, neither where it was: the first
    [InlineData("SELECT 1 FROM dual;\nSELECT 2 FROM dual WHERE 1 = 1;", 20, -1)] // changed
    [InlineData("SELECT 1 FROM dual;", 20, -1)] // gone
    public void A_result_s_statement_is_found_again_only_with_its_exact_text(string script, int was, int expected)
    {
        var found = SqlScript.Find(script, new SqlStatement(was, 18, "SELECT 2 FROM dual"));

        Assert.Equal(expected, found?.Start ?? -1);
    }

    /// <summary>
    /// Review WP-81: a result's statement taken back into the editor never goes inside a statement – inserted at a cursor
    /// in the middle, <see cref="SqlScript.Insert"/> ended the text before it with ";" and cut the user's statement in two.
    /// </summary>
    [Theory]
    [InlineData("SELECT 1 FROM t WHERE x = 2;\nSELECT 2 FROM dual;", 22, 29)] // inside: before the next statement
    [InlineData("SELECT 1 FROM t WHERE x = 2;\nSELECT 2 FROM dual;", 27, 29)] // just before its ";"
    [InlineData("SELECT 1 FROM t WHERE x = 2", 10, 27)] // the last one, without ";": at the end
    [InlineData("SELECT 1 FROM dual  ;\nSELECT 2 FROM dual;", 19, 22)] // blanks before its ";": behind the ";" (second review)
    [InlineData("SELECT 1 FROM dual\n;", 19, 20)] // its ";" on a line of its own
    [InlineData("BEGIN NULL; END;\n/\nSELECT 2 FROM dual;", 17, 19)] // a block, before its "/" line
    [InlineData("SELECT 1 FROM t WHERE x = 2;\n", 28, 28)] // behind its ";": at the cursor
    [InlineData("SELECT 1 FROM t;\n\nSELECT 2 FROM dual;", 17, 17)] // between statements: at the cursor
    [InlineData("SELECT 1 FROM t;", 0, 0)] // before a statement: at the cursor
    public void A_statement_taken_back_never_goes_inside_another(string script, int cursor, int expected)
    {
        var at = SqlScript.InsertionPoint(script, cursor);

        Assert.Equal(expected, at);
        var (text, _) = SqlScript.Insert(script, at, at, "SELECT 9 FROM dual");
        Assert.Equal(SqlScript.Split(script).Count + 1, SqlScript.Split(text).Count); // nothing cut in two
    }

    /// <summary>
    /// Review: a block behind a statement without ";" fell apart at its inner ";" – Ctrl+Enter on its DELETE line ran the
    /// DELETE. Decision of the user: a PL/SQL object's CREATE starts a statement of its own, a BEGIN block is refused.
    /// </summary>
    [Fact]
    public void A_block_behind_a_statement_without_semicolon_is_never_cut_into_statements()
    {
        const string script = """
            SELECT 1 FROM dual

            -- the trigger
            CREATE OR REPLACE TRIGGER trg AFTER INSERT ON t FOR EACH ROW
            BEGIN
              INSERT INTO log VALUES (1);
              DELETE FROM audit_log WHERE id < 30;
            END;
            /
            UPDATE t SET a = 1
            BEGIN
              <<lbl>>
              INSERT INTO log VALUES (1);
              DELETE FROM audit_log WHERE id < 30;
            END;
            /
            SELECT id,
            begin
              FROM t;
            SELECT * FROM t ORDER BY id,
            begin
            DESC;
            CREATE TABLE z (
              id NUMBER,
              begin DATE
            );
            SELECT * FROM z
            BEGIN
              offset := 0;
              DELETE FROM audit_log WHERE id < 30;
            END;
            /
            """;
        var text = script.ReplaceLineEndings("\n");

        var statements = SqlScript.Split(text);

        Assert.Equal(
            [
                "SELECT 1 FROM dual",
                "-- the trigger\nCREATE OR REPLACE TRIGGER trg AFTER INSERT ON t FOR EACH ROW\nBEGIN\n  INSERT INTO log VALUES (1);\n  DELETE FROM audit_log WHERE id < 30;\nEND;",
                "UPDATE t SET a = 1\nBEGIN\n  <<lbl>>\n  INSERT INTO log VALUES (1);\n  DELETE FROM audit_log WHERE id < 30;\nEND;",
                "SELECT id,\nbegin\n  FROM t",
                "SELECT * FROM t ORDER BY id,\nbegin\nDESC",
                "CREATE TABLE z (\n  id NUMBER,\n  begin DATE\n)",
                "SELECT * FROM z\nBEGIN\n  offset := 0;\n  DELETE FROM audit_log WHERE id < 30;\nEND;",
            ],
            statements.Select(s => s.Text));
        Assert.Equal(SqlStatementKind.Trigger, SqlScript.Analyze(statements[1].Text).Kind);
        var merged = SqlScript.Analyze(statements[2].Text);
        Assert.Equal((SqlStatementKind.Update, true), (merged.Kind, merged.RunsIntoBlock));
        Assert.StartsWith("Vor dem PL/SQL-Block (BEGIN/DECLARE) fehlt ein „;“", merged.Rejection);
        Assert.NotNull(SqlScript.DdlRejection(statements[2].Text));
        var delete = text.IndexOf("DELETE", text.IndexOf("UPDATE", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Equal(statements[2].Text, SqlScript.StatementAt(text, delete, delete)?.Text);
        Assert.Null(SqlScript.Analyze(statements[3].Text).Rejection);
        Assert.Null(SqlScript.Analyze(statements[4].Text).Rejection); // DESC ends SQL, never a PL/SQL call
        Assert.True(SqlScript.Analyze(statements[6].Text).RunsIntoBlock); // "offset" continues SQL, ":=" does not
    }

    /// <summary>
    /// WP-26: a trigger (or another block) holds ";" and blank lines and ends at a "/" line – cut at its first ";", it would
    /// reach Oracle as a broken trigger.
    /// </summary>
    [Fact]
    public void A_block_ends_only_at_a_slash_line()
    {
        const string script = """
            CREATE OR REPLACE TRIGGER kunde_audit
            AFTER INSERT OR UPDATE ON kunde
            FOR EACH ROW
            BEGIN
              IF INSERTING THEN x := 'I';

              END IF;
            END;
            /
            SELECT 1 FROM dual;
            BEGIN NULL; END;
            /
            ALTER TRIGGER kunde_audit ENABLE
            -- a package
            CREATE PACKAGE p AS
              x NUMBER;
            END;
            """;

        Assert.Equal(
            [
                "CREATE OR REPLACE TRIGGER kunde_audit\nAFTER INSERT OR UPDATE ON kunde\nFOR EACH ROW\nBEGIN\n  IF INSERTING THEN x := 'I';\n\n  END IF;\nEND;",
                "SELECT 1 FROM dual",
                "BEGIN NULL; END;",
                "ALTER TRIGGER kunde_audit ENABLE",
                "-- a package\nCREATE PACKAGE p AS\n  x NUMBER;\nEND;",
            ],
            SqlScript.Split(script.ReplaceLineEndings("\n")).Select(s => s.Text));
    }

    [Theory]
    [InlineData("SELECT 1 FROM dual;|\nSELECT 2 FROM dual;", "SELECT 1 FROM dual")]
    [InlineData("SELECT 1 FROM dual;\n|", "SELECT 1 FROM dual")]
    [InlineData("SELECT 1\n  FROM du|al;\nSELECT 2 FROM dual;", "SELECT 1\n  FROM dual")]
    [InlineData("SELECT 1 FROM dual;\nSEL|ECT 2 FROM dual;", "SELECT 2 FROM dual")]
    [InlineData("SELECT 1 FROM dual;\n\n|\n\nSELECT 2 FROM dual", null)]
    [InlineData("SELECT 1 FROM dual\n\n|\n\nSELECT 2 FROM dual", "SELECT 1 FROM dual\n\n\n\nSELECT 2 FROM dual")]
    [InlineData("|\nSELECT 2 FROM dual", "SELECT 2 FROM dual")]
    [InlineData("|", null)]
    public void The_statement_at_the_cursor(string marked, string? expected)
    {
        var cursor = marked.IndexOf('|', StringComparison.Ordinal);
        var text = marked.Remove(cursor, 1);

        Assert.Equal(expected, SqlScript.StatementAt(text, cursor, cursor)?.Text);
    }

    [Fact]
    public void A_selection_runs_as_written()
    {
        const string text = "SELECT a, b FROM t WHERE x = 1;";
        var start = text.IndexOf("SELECT a", StringComparison.Ordinal);

        var statement = SqlScript.StatementAt(text, start, text.IndexOf(" WHERE", StringComparison.Ordinal));

        Assert.Equal(("SELECT a, b FROM t", 0), (statement?.Text, statement?.Start));
        Assert.Equal(2, SqlScript.CountStatements("SELECT 1 FROM dual; SELECT 2 FROM dual"));
    }

    [Fact]
    public void A_script_runs_the_selected_statements_or_all_with_their_positions()
    {
        const string text = "SELECT 1 FROM dual;\nUPDATE t SET x = 1;\n\nDELETE FROM t";

        Assert.Equal(["SELECT 1 FROM dual", "UPDATE t SET x = 1", "DELETE FROM t"], SqlScript.StatementsIn(text, 5, 5).Select(s => s.Text));
        var selected = SqlScript.StatementsIn(text, text.IndexOf("UPDATE", StringComparison.Ordinal), text.Length);
        Assert.Equal(["UPDATE t SET x = 1", "DELETE FROM t"], selected.Select(s => s.Text));
        Assert.All(selected, s => Assert.Equal(s.Text, text.Substring(s.Start, s.Length)));
        Assert.Empty(SqlScript.StatementsIn("  -- nur ein Kommentar\n", 0, 0));
    }

    [Theory]
    [InlineData("select * from t", SqlStatementKind.Query)]
    [InlineData("/* hint */ WITH x AS (SELECT 1 FROM dual) SELECT * FROM x", SqlStatementKind.Query)]
    [InlineData("insert into t values (1)", SqlStatementKind.Insert)]
    [InlineData("UPDATE t SET x = 1", SqlStatementKind.Update)]
    [InlineData("DELETE t", SqlStatementKind.Delete)]
    [InlineData("MERGE INTO t USING s ON (t.id = s.id) WHEN MATCHED THEN UPDATE SET x = s.x", SqlStatementKind.Merge)]
    [InlineData("CREATE TABLE t (x NUMBER)", SqlStatementKind.Ddl)]
    [InlineData("ALTER TABLE t ADD y NUMBER", SqlStatementKind.Ddl)]
    [InlineData("ALTER SESSION SET nls_date_format = 'YYYY'", SqlStatementKind.SessionControl)]
    [InlineData("TRUNCATE TABLE t", SqlStatementKind.Truncate)]
    [InlineData("COMMENT ON COLUMN t.x IS 'Kunde'", SqlStatementKind.Ddl)]
    [InlineData("CREATE OR REPLACE VIEW v AS SELECT 1 x FROM dual", SqlStatementKind.Ddl)]
    [InlineData("CREATE INDEX t_x ON t (x)", SqlStatementKind.Ddl)]
    [InlineData("DROP PROCEDURE p", SqlStatementKind.Ddl)]
    [InlineData("ALTER TRIGGER t_bi DISABLE", SqlStatementKind.Ddl)]
    [InlineData("CREATE OR REPLACE PROCEDURE p AS BEGIN NULL", SqlStatementKind.PlSqlObject)]
    [InlineData("create function f return number is begin return 1", SqlStatementKind.PlSqlObject)]
    [InlineData("CREATE OR REPLACE NONEDITIONABLE PACKAGE BODY pkg AS", SqlStatementKind.PlSqlObject)]
    [InlineData("CREATE TRIGGER t_bi BEFORE INSERT ON t FOR EACH ROW BEGIN NULL", SqlStatementKind.Trigger)]
    [InlineData("CREATE OR REPLACE EDITIONABLE TRIGGER \"APP\".\"T_AUDIT\" AFTER UPDATE OF a ON app.t BEGIN NULL", SqlStatementKind.Trigger)]
    [InlineData("CREATE TRIGGER v_io INSTEAD OF INSERT ON v FOR EACH ROW BEGIN NULL", SqlStatementKind.Trigger)]
    [InlineData("CREATE TRIGGER t_bi BEFORE INSERT ON \"SCHEMA\" FOR EACH ROW BEGIN NULL", SqlStatementKind.Trigger)]
    [InlineData("CREATE TRIGGER t_c FOR UPDATE ON t COMPOUND TRIGGER AFTER STATEMENT IS BEGIN NULL", SqlStatementKind.Trigger)]
    [InlineData("CREATE OR REPLACE TRIGGER logon_t AFTER LOGON ON DATABASE BEGIN NULL", SqlStatementKind.SystemTrigger)]
    [InlineData("create trigger ddl_t before ddl on schema begin null", SqlStatementKind.SystemTrigger)]
    [InlineData("CREATE TRIGGER ddl_t BEFORE DROP ON app.SCHEMA BEGIN NULL", SqlStatementKind.SystemTrigger)]
    [InlineData("CREATE TRIGGER pdb_t AFTER STARTUP ON PLUGGABLE DATABASE BEGIN NULL", SqlStatementKind.SystemTrigger)]
    [InlineData("CREATE TYPE adresse AS OBJECT (ort VARCHAR2(40))", SqlStatementKind.PlSqlObject)]
    [InlineData("CREATE OR REPLACE AND COMPILE JAVA SOURCE NAMED x AS class X {}", SqlStatementKind.PlSqlObject)]
    [InlineData("CREATE MLE MODULE m LANGUAGE JAVASCRIPT AS export function f() { return 1 }", SqlStatementKind.PlSqlObject)]
    [InlineData("CREATE OR REPLACE MLE ENV e IMPORTS ('m' MODULE m)", SqlStatementKind.PlSqlObject)]
    [InlineData("CREATE LIBRARY ext AS '/u01/lib/ext.so'", SqlStatementKind.PlSqlObject)]
    [InlineData("BEGIN NULL; END", SqlStatementKind.PlSql)]
    [InlineData("EXEC dbms_stats.gather_table_stats('A', 'T')", SqlStatementKind.Call)]
    [InlineData("COMMIT", SqlStatementKind.TransactionControl)]
    [InlineData("SET TRANSACTION READ ONLY", SqlStatementKind.TransactionControl)]
    [InlineData("LOCK TABLE t IN EXCLUSIVE MODE", SqlStatementKind.Lock)]
    [InlineData("EXPLAIN PLAN FOR SELECT 1 FROM dual", SqlStatementKind.Explain)]
    [InlineData("SHOW ERRORS", SqlStatementKind.Unknown)]
    [InlineData("  -- nothing\n", SqlStatementKind.Empty)]
    public void Kinds(string statement, SqlStatementKind kind) => Assert.Equal(kind, SqlScript.Analyze(statement).Kind);

    [Fact]
    public void Queries_dml_and_ddl_run_others_say_why()
    {
        Assert.Null(SqlScript.Analyze("SELECT 1 FROM dual").Rejection);
        Assert.Null(SqlScript.Analyze("MERGE INTO t USING s ON (1 = 1) WHEN MATCHED THEN UPDATE SET x = 1").Rejection);
        Assert.Null(SqlScript.Analyze("drop table t").Rejection); // WP-22
        Assert.True(SqlScript.Analyze("ALTER TABLE t ADD y NUMBER").IsDdl);
        Assert.StartsWith("TRUNCATE führt der SQL-Editor nicht aus", SqlScript.Analyze("truncate table t").Rejection);
        Assert.StartsWith("Statements, die Zeilen oder gelöschte Objekte ohne Rückweg entfernen", SqlScript.Analyze("ALTER TABLE t TRUNCATE PARTITION p").Rejection); // A9
        Assert.StartsWith("PL/SQL-Objekte", SqlScript.Analyze("CREATE OR REPLACE PROCEDURE p AS BEGIN NULL").Rejection);
        Assert.Null(SqlScript.Analyze("CREATE OR REPLACE TRIGGER x BEFORE INSERT ON t BEGIN NULL").Rejection); // WP-26
        Assert.True(SqlScript.Analyze("CREATE OR REPLACE TRIGGER x BEFORE INSERT ON t BEGIN NULL").IsDdl);
        Assert.StartsWith("Trigger auf der Datenbank", SqlScript.Analyze("CREATE TRIGGER x AFTER LOGON ON DATABASE BEGIN NULL").Rejection);
        Assert.Equal("DDL nimmt keine Bind-Variablen (:n) – den Wert direkt ins Statement schreiben.",
            SqlScript.Analyze("ALTER TABLE t MODIFY x DEFAULT :n").Rejection);
        Assert.Null(SqlScript.Analyze("ALTER TABLE t MODIFY x DEFAULT ':n'").Rejection); // a literal, no bind
        Assert.Contains("Statusleiste", SqlScript.Analyze("rollback").Rejection);
        Assert.Contains("FOR UPDATE", SqlScript.Analyze("SELECT * FROM t WHERE id = 1 FOR UPDATE NOWAIT").Rejection);
        Assert.False(SqlScript.Analyze("SELECT * FROM t FOR UPDATE").IsQuery);
        Assert.Contains("„SHOW“", SqlScript.Analyze("show errors").Rejection);
    }

    /// <summary>WP-22: the table a DDL changes – to take it before ALTER TABLE and to name who holds it.</summary>
    [Theory]
    [InlineData("ALTER TABLE kunden ADD (fax VARCHAR2(30))", null, "KUNDEN")]
    [InlineData("alter table app.\"Kunden\" modify (name varchar2(80))", "APP", "Kunden")]
    [InlineData("DROP TABLE kunden PURGE", null, "KUNDEN")]
    [InlineData("TRUNCATE TABLE app.kunden", "APP", "KUNDEN")]
    [InlineData("RENAME kunden TO kunden_alt", null, "KUNDEN")]
    [InlineData("CREATE INDEX kunden_name ON kunden (name)", null, "KUNDEN")]
    [InlineData("CREATE UNIQUE INDEX app.kunden_mail ON app.kunden (email)", "APP", "KUNDEN")]
    [InlineData("COMMENT ON TABLE kunden IS 'a; b'", null, "KUNDEN")]
    [InlineData("COMMENT ON COLUMN kunden.fax IS 'Fax'", null, "KUNDEN")]
    [InlineData("COMMENT ON COLUMN app.kunden.fax IS 'Fax'", "APP", "KUNDEN")]
    [InlineData("-- vorher\nFLASHBACK TABLE kunden TO BEFORE DROP", null, "KUNDEN")]
    // R4a: keywords that end a table reference in a query are names here, and 23ai's IF [NOT] EXISTS is skipped.
    [InlineData("ALTER TABLE log ADD (x NUMBER)", null, "LOG")]
    [InlineData("ALTER TABLE app.log ADD (x NUMBER)", "APP", "LOG")]
    [InlineData("ALTER TABLE IF EXISTS kunden ADD (x NUMBER)", null, "KUNDEN")]
    [InlineData("DROP TABLE IF EXISTS app.window", "APP", "WINDOW")]
    [InlineData("CREATE INDEX ix_log ON log (a)", null, "LOG")]
    [InlineData("CREATE INDEX IF NOT EXISTS ix_log ON app.log (a)", "APP", "LOG")]
    [InlineData("COMMENT ON COLUMN log.x IS 'x'", null, "LOG")]
    [InlineData("ALTER TABLE if ADD (x NUMBER)", null, "IF")]
    public void Ddl_names_the_table_it_changes(string sql, string? owner, string name) =>
        Assert.Equal(new SqlTableReference(owner, name, null, 0), SqlScript.DdlTableOf(sql));

    [Theory]
    [InlineData("CREATE TABLE neu (id NUMBER)")]
    [InlineData("CREATE OR REPLACE VIEW v AS SELECT * FROM kunden")]
    [InlineData("GRANT SELECT ON kunden TO x")]
    [InlineData("ALTER INDEX kunden_name REBUILD")]
    [InlineData("ALTER SESSION SET CURRENT_SCHEMA = x")]
    [InlineData("UPDATE kunden SET a = 1")]
    [InlineData("SELECT * FROM kunden")]
    [InlineData("")]
    public void Other_statements_name_no_table(string sql) => Assert.Null(SqlScript.DdlTableOf(sql));

    [Fact]
    public void Binds_in_order_without_duplicates_ignoring_case()
    {
        var info = SqlScript.Analyze("SELECT * FROM t WHERE a = :Id OR b = :name OR c = :id OR d = ':no' -- :no\n");

        Assert.Equal(["Id", "name"], info.Binds);
    }

    /// <summary>WP-26: :new and :old (and the names of REFERENCING) are the trigger's rows – the editor must not ask for them.</summary>
    [Fact]
    public void A_trigger_has_no_binds()
    {
        var info = SqlScript.Analyze("""
            CREATE OR REPLACE TRIGGER kunde_audit AFTER UPDATE ON kunde REFERENCING NEW AS n FOR EACH ROW
            BEGIN
              IF :n.name <> :old.name THEN INSERT INTO audit_kunde (name) VALUES (:n.name); END IF;
            END;
            """);

        Assert.Equal(SqlStatementKind.Trigger, info.Kind);
        Assert.Empty(info.Binds);
        Assert.Empty(info.BindUses);
        Assert.Null(info.Rejection);
    }

    [Theory]
    [InlineData("UPDATE t SET x = 1", false)]
    [InlineData("UPDATE t SET x = (SELECT y FROM s WHERE s.id = 1)", false)]
    [InlineData("UPDATE t SET x = 1 WHERE id = 1", true)]
    [InlineData("DELETE FROM t", false)]
    [InlineData("delete from t where id in (select id from s where z = 1)", true)]
    public void Where_counts_only_at_the_top_level(string statement, bool hasWhere)
    {
        var info = SqlScript.Analyze(statement);

        Assert.Equal(hasWhere, info.HasWhere);
        Assert.Equal(!hasWhere, info.AffectsAllRows);
    }

    [Fact]
    public void Tables_with_owners_aliases_joins_and_subqueries()
    {
        var info = SqlScript.Analyze("""
            SELECT k.name, a.status
              FROM ferret.kunden k
              JOIN "Auftrag" AS a ON a.kunde_id = k.kunde_id
              LEFT OUTER JOIN adresse ON adresse.id = k.adresse_id,
                   dual
             WHERE EXISTS (SELECT 1 FROM rechnung r WHERE r.kunde_id = k.kunde_id)
             ORDER BY 1
            """);

        Assert.Equal(
            [
                new SqlTableReference("FERRET", "KUNDEN", "K", 0),
                new SqlTableReference(null, "Auftrag", "A", 0),
                new SqlTableReference(null, "ADRESSE", null, 0),
                new SqlTableReference(null, "DUAL", null, 0),
                new SqlTableReference(null, "RECHNUNG", "R", 1),
            ],
            info.Tables);
    }

    [Theory]
    [InlineData("INSERT INTO kunden (id) VALUES (1)", "KUNDEN", null)]
    [InlineData("UPDATE kunden k SET k.name = 'x'", "KUNDEN", "K")]
    [InlineData("DELETE FROM kunden WHERE id = 1", "KUNDEN", null)]
    [InlineData("SELECT * FROM kunden AS OF TIMESTAMP SYSDATE - 1", "KUNDEN", null)]
    [InlineData("SELECT * FROM (SELECT * FROM kunden) x", "KUNDEN", null)]
    public void Tables_of_dml_and_special_forms(string statement, string name, string? alias)
    {
        var table = Assert.Single(SqlScript.Analyze(statement).Tables);

        Assert.Equal((name, alias), (table.Name, table.Alias));
    }

    [Theory]
    [InlineData("SELECT x.n FROM (SELECT name AS n FROM kunden) x", "X")]
    [InlineData("""SELECT "t"."ID" FROM (SELECT "k"."ID" FROM "KUNDEN" "k" FETCH FIRST 5 ROWS ONLY) "t" LEFT JOIN "AUFTRAG" "a" ON 1 = 1""", "t")]
    [InlineData("SELECT * FROM kunden k JOIN (SELECT * FROM auftrag) AS a ON 1 = 1", "A")]
    [InlineData("SELECT * FROM kunden k, (SELECT 1 AS n FROM dual) d", "D")]
    [InlineData("SELECT * FROM kunden k CROSS APPLY (SELECT a.id FROM auftrag a WHERE a.kunde_id = k.id) x", "X")]
    [InlineData("SELECT * FROM kunden k, LATERAL (SELECT a.id FROM auftrag a WHERE a.kunde_id = k.id) l", "L")]
    public void Subqueries_standing_as_row_sources_are_inline_views(string statement, string alias)
    {
        Assert.Equal([alias], SqlScript.Analyze(statement).InlineViews);
    }

    [Theory]
    [InlineData("SELECT (SELECT 1 FROM dual) AS n, k.id FROM kunden k")]
    [InlineData("SELECT * FROM kunden WHERE id IN (SELECT kunde_id FROM auftrag) AND 1 = 1")]
    [InlineData("SELECT * FROM kunden WHERE EXISTS (SELECT 1 FROM auftrag) ORDER BY 1")]
    public void Other_subqueries_are_no_inline_views(string statement)
    {
        Assert.Empty(SqlScript.Analyze(statement).InlineViews);
    }

    [Fact]
    public void Merge_names_target_and_source()
    {
        var info = SqlScript.Analyze("MERGE INTO kunden k USING neu n ON (k.id = n.id) WHEN MATCHED THEN UPDATE SET k.name = n.name");

        Assert.Equal([("KUNDEN", "K"), ("NEU", "N")], info.Tables.Select(t => (t.Name, t.Alias)));
    }

    [Fact]
    public void Binds_compared_with_columns_suggest_their_types()
    {
        var info = SqlScript.Analyze("""
            SELECT * FROM kunden k
             WHERE k.kunde_id = :id
               AND :name = name
               AND erstellt_am BETWEEN :von AND :bis
               AND k.kundenart IN (:art1, 2, :art2)
               AND umsatz > :min
               AND upper(name) LIKE :muster
            """);

        Assert.Equal(
            [
                new SqlBindUse("id", "K", "KUNDE_ID"),
                new SqlBindUse("name", null, "NAME"),
                new SqlBindUse("von", null, "ERSTELLT_AM"),
                new SqlBindUse("bis", null, "ERSTELLT_AM"),
                new SqlBindUse("art1", "K", "KUNDENART"),
                new SqlBindUse("art2", "K", "KUNDENART"),
                new SqlBindUse("min", null, "UMSATZ"),
            ],
            info.BindUses);
    }

    /// <summary>R4a: callers with several statements check all of them before the first runs (each DDL commits at once).</summary>
    [Fact]
    public void Ddl_rejection_says_why_the_schema_path_would_refuse_a_statement()
    {
        Assert.Null(SqlScript.DdlRejection("ALTER TABLE kunden ADD (fax VARCHAR2(30));"));
        Assert.Null(SqlScript.DdlRejection("-- Kommentar\nCOMMENT ON TABLE kunden IS 'a;b'"));
        Assert.Equal("Das sind mehrere Anweisungen – ein „;“ oder eine Zeile nur mit „/“ darin beendet die Anweisung dort.",
            SqlScript.DdlRejection("ALTER TABLE kunden ADD CHECK (status IN ('A','B');)"));
        Assert.Equal("„UPDATE“ ist keine DDL-Anweisung.", SqlScript.DdlRejection("UPDATE kunden SET name = 'x'"));
        Assert.Equal("DDL nimmt keine Bind-Variablen (:v) – den Wert direkt ins Statement schreiben.",
            SqlScript.DdlRejection("ALTER TABLE kunden MODIFY (name DEFAULT :v)"));
        Assert.Null(SqlScript.DdlRejection("CREATE OR REPLACE TRIGGER t BEFORE INSERT ON kunden BEGIN NULL; END;")); // WP-26
        Assert.NotNull(SqlScript.DdlRejection("CREATE OR REPLACE PROCEDURE p AS BEGIN NULL; END;"));
        Assert.NotNull(SqlScript.DdlRejection("CREATE TRIGGER t AFTER LOGON ON DATABASE BEGIN NULL; END;"));
    }

    [Theory]
    [InlineData("-- nur ein Kommentar", SqlStatementKind.Empty)]
    [InlineData("/* x */ WITH a AS (SELECT 1 FROM dual) SELECT * FROM a", SqlStatementKind.Query)]
    [InlineData("alter session set nls_sort = binary", SqlStatementKind.SessionControl)]
    [InlineData("CREATE OR REPLACE EDITIONABLE PACKAGE p AS END;", SqlStatementKind.PlSqlObject)]
    [InlineData("CREATE UNIQUE INDEX ix ON t (a)", SqlStatementKind.Ddl)]
    [InlineData("ALTER TABLE t TRUNCATE PARTITION p2025", SqlStatementKind.Truncate)]
    [InlineData("alter table t drop subpartition sp1 update indexes", SqlStatementKind.Truncate)]
    [InlineData("ALTER TABLE t DROP PARTITIONS p1, p2", SqlStatementKind.Truncate)]
    [InlineData("FLASHBACK TABLE t TO TIMESTAMP SYSTIMESTAMP - INTERVAL '1' HOUR", SqlStatementKind.Truncate)]
    [InlineData("FLASHBACK TABLE t TO SCN 12345", SqlStatementKind.Truncate)]
    [InlineData("PURGE RECYCLEBIN", SqlStatementKind.Truncate)]
    [InlineData("PURGE TABLE t", SqlStatementKind.Truncate)]
    // Decision of the user (review 3.40.0, SE-8): past the recycle bin, a schema with all its objects.
    [InlineData("DROP TABLE t PURGE", SqlStatementKind.Truncate)]
    [InlineData("drop table app.t cascade constraints purge", SqlStatementKind.Truncate)]
    [InlineData("DROP USER app CASCADE", SqlStatementKind.Truncate)]
    // The same (review 3.44.0, A14, decision of the user).
    [InlineData("DROP TABLESPACE ts INCLUDING CONTENTS AND DATAFILES", SqlStatementKind.Truncate)]
    [InlineData("drop cluster c including tables cascade constraints", SqlStatementKind.Truncate)]
    [InlineData("DROP PLUGGABLE DATABASE pdb1 INCLUDING DATAFILES", SqlStatementKind.Truncate)]
    [InlineData("DROP TABLESPACE ts", SqlStatementKind.Ddl)] // fails while it holds objects
    [InlineData("DROP PLUGGABLE DATABASE pdb1 KEEP DATAFILES", SqlStatementKind.Ddl)]
    [InlineData("ALTER DATABASE CLOSE", SqlStatementKind.SessionControl)]
    [InlineData("ALTER PLUGGABLE DATABASE pdb1 CLOSE IMMEDIATE", SqlStatementKind.SessionControl)]
    // A link's DDL like ALTER PUBLIC DATABASE LINK (review 3.54.0, A13, decision of the user).
    [InlineData("ALTER DATABASE LINK remote CONNECT TO app IDENTIFIED BY geheim", SqlStatementKind.Ddl)]
    [InlineData("ALTER PUBLIC DATABASE LINK remote CONNECT TO app IDENTIFIED BY geheim", SqlStatementKind.Ddl)]
    [InlineData("ALTER DATABASE OPEN READ ONLY", SqlStatementKind.SessionControl)]
    [InlineData("DROP TABLE t", SqlStatementKind.Ddl)] // the recycle bin keeps it (FLASHBACK … TO BEFORE DROP)
    [InlineData("DROP TABLE t CASCADE CONSTRAINTS", SqlStatementKind.Ddl)]
    [InlineData("DROP USER app", SqlStatementKind.Ddl)] // fails if the user owns objects
    [InlineData("FLASHBACK TABLE t TO BEFORE DROP", SqlStatementKind.Ddl)]
    [InlineData("ALTER TABLE t DROP COLUMN partition_key", SqlStatementKind.Ddl)]
    [InlineData("ALTER TABLE t ADD PARTITION p2026 VALUES LESS THAN (2027)", SqlStatementKind.Ddl)]
    [InlineData("TRUNCATE TABLE t", SqlStatementKind.Truncate)]
    public void The_classifier_tells_the_kind_from_the_first_words(string statement, SqlStatementKind kind) =>
        Assert.Equal(kind, SqlClassifier.KindOf(statement));

    /// <summary>
    /// R4b: the session's statement guards depend only on the tokenizer and the classifier – a change to the analysis
    /// (tables, binds, DDL targets) or the splitter must never loosen what the read, write or schema path takes.
    /// </summary>
    [Fact]
    public void The_statement_guards_use_only_the_tokenizer_and_the_classifier()
    {
        var guard = File.ReadAllText(Path.Combine(Repository.Root, "src", "FerretSharp.Core", "Oracle", "StatementGuard.cs"));
        var code = string.Join("\n", guard.Split('\n').Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

        Assert.Contains("SqlTokenizer.", code, StringComparison.Ordinal);
        Assert.Contains("SqlClassifier.", code, StringComparison.Ordinal);
        Assert.DoesNotContain("SqlScript.", code, StringComparison.Ordinal);
        Assert.DoesNotContain("SqlAnalysis.", code, StringComparison.Ordinal);
        Assert.DoesNotContain("SqlSplitter.", code, StringComparison.Ordinal);
    }
}
