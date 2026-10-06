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
    public void Statements_end_at_semicolons_blank_lines_and_slash_lines_not_inside_literals_or_comments()
    {
        const string script = """
            SELECT 'a;b' FROM dual; SELECT 2 FROM dual;
            -- the next one
            UPDATE t
               SET x = 1 /* stays

            in here */
            /
            DELETE FROM t

            -- only a comment

            SELECT 3 FROM dual
            """;

        Assert.Equal(
            [
                "SELECT 'a;b' FROM dual",
                "SELECT 2 FROM dual",
                "-- the next one\nUPDATE t\n   SET x = 1 /* stays\n\nin here */",
                "DELETE FROM t",
                "SELECT 3 FROM dual",
            ],
            SqlScript.Split(script.ReplaceLineEndings("\n")).Select(s => s.Text));
    }

    [Theory]
    [InlineData("SELECT 1 FROM dual;|\nSELECT 2 FROM dual;", "SELECT 1 FROM dual")]
    [InlineData("SELECT 1 FROM dual;\n|", "SELECT 1 FROM dual")]
    [InlineData("SELECT 1\n  FROM du|al;\nSELECT 2 FROM dual;", "SELECT 1\n  FROM dual")]
    [InlineData("SELECT 1 FROM dual;\nSEL|ECT 2 FROM dual;", "SELECT 2 FROM dual")]
    [InlineData("SELECT 1 FROM dual\n\n|\n\nSELECT 2 FROM dual", null)]
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
    [InlineData("TRUNCATE TABLE t", SqlStatementKind.Ddl)]
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
    public void Only_queries_and_dml_run_others_say_why()
    {
        Assert.Null(SqlScript.Analyze("SELECT 1 FROM dual").Rejection);
        Assert.Null(SqlScript.Analyze("MERGE INTO t USING s ON (1 = 1) WHEN MATCHED THEN UPDATE SET x = 1").Rejection);
        Assert.Contains("committet implizit", SqlScript.Analyze("drop table t").Rejection);
        Assert.StartsWith("DROP (DDL)", SqlScript.Analyze("drop table t").Rejection);
        Assert.Contains("Statusleiste", SqlScript.Analyze("rollback").Rejection);
        Assert.Contains("FOR UPDATE", SqlScript.Analyze("SELECT * FROM t WHERE id = 1 FOR UPDATE NOWAIT").Rejection);
        Assert.False(SqlScript.Analyze("SELECT * FROM t FOR UPDATE").IsQuery);
        Assert.Contains("„SHOW“", SqlScript.Analyze("show errors").Rejection);
    }

    [Fact]
    public void Binds_in_order_without_duplicates_ignoring_case()
    {
        var info = SqlScript.Analyze("SELECT * FROM t WHERE a = :Id OR b = :name OR c = :id OR d = ':no' -- :no\n");

        Assert.Equal(["Id", "name"], info.Binds);
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
}
