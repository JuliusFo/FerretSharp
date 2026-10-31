using FerretSharp.Core.Connections;
using FerretSharp.Core.Ddl;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;
using FerretSharp.Testing;

namespace FerretSharp.Core.Tests.Ddl;

/// <summary>
/// WP-26 (ADR 0021): a trigger is created with DISABLE and enabled only once it compiled. <see cref="TriggerDdl"/> puts
/// DISABLE into the header without touching anything else and reads the trigger's name.
/// </summary>
public sealed class TriggerDdlTests
{
    private const string Audit = """
        CREATE OR REPLACE TRIGGER kunde_aud
          AFTER INSERT OR UPDATE OR DELETE ON kunde
          FOR EACH ROW
        BEGIN
          INSERT INTO kunde_aud VALUES (:new.id, :old.id);
        END;
        """;

    [Fact]
    public void Disable_goes_behind_the_last_word_of_the_header_and_moves_no_line_of_the_body()
    {
        var disabled = TriggerDdl.WithDisable(Audit);

        Assert.Equal(Audit.Replace("FOR EACH ROW", "FOR EACH ROW DISABLE", StringComparison.Ordinal), disabled);
        Assert.Equal(Audit.Split('\n').Length, disabled!.Split('\n').Length);
    }

    [Theory]
    // EDITIONABLE, quoted names and owner, REFERENCING with renamed rows
    [InlineData(
        """CREATE OR REPLACE EDITIONABLE TRIGGER "App"."Kunde Aud" BEFORE UPDATE OF name ON "App".kunde REFERENCING NEW AS n OLD AS o FOR EACH ROW BEGIN :n.name := UPPER(:n.name); END;""",
        """CREATE OR REPLACE EDITIONABLE TRIGGER "App"."Kunde Aud" BEFORE UPDATE OF name ON "App".kunde REFERENCING NEW AS n OLD AS o FOR EACH ROW DISABLE BEGIN :n.name := UPPER(:n.name); END;""")]
    // WHEN comes after DISABLE; the parentheses of WHEN hide nothing
    [InlineData(
        "CREATE TRIGGER t1 BEFORE INSERT ON kunde FOR EACH ROW WHEN (new.id > 0) BEGIN NULL; END;",
        "CREATE TRIGGER t1 BEFORE INSERT ON kunde FOR EACH ROW DISABLE WHEN (new.id > 0) BEGIN NULL; END;")]
    // DECLARE starts the body
    [InlineData(
        "CREATE TRIGGER t1 AFTER DELETE ON kunde DECLARE n NUMBER; BEGIN n := 1; END;",
        "CREATE TRIGGER t1 AFTER DELETE ON kunde DISABLE DECLARE n NUMBER; BEGIN n := 1; END;")]
    // a label before the block, FOLLOWS another trigger
    [InlineData(
        "CREATE TRIGGER t2 AFTER INSERT ON kunde FOR EACH ROW FOLLOWS t1 <<main>> BEGIN GOTO done; <<done>> NULL; END;",
        "CREATE TRIGGER t2 AFTER INSERT ON kunde FOR EACH ROW FOLLOWS t1 DISABLE <<main>> BEGIN GOTO done; <<done>> NULL; END;")]
    // compound trigger
    [InlineData(
        "CREATE OR REPLACE TRIGGER t3 FOR UPDATE OF name ON kunde COMPOUND TRIGGER AFTER EACH ROW IS BEGIN NULL; END AFTER EACH ROW; END t3;",
        "CREATE OR REPLACE TRIGGER t3 FOR UPDATE OF name ON kunde DISABLE COMPOUND TRIGGER AFTER EACH ROW IS BEGIN NULL; END AFTER EACH ROW; END t3;")]
    // CALL as the body, INSTEAD OF on a view
    [InlineData(
        "CREATE TRIGGER t4 INSTEAD OF INSERT ON kunde_v FOR EACH ROW CALL kunde_neu(:new.id)",
        "CREATE TRIGGER t4 INSTEAD OF INSERT ON kunde_v FOR EACH ROW DISABLE CALL kunde_neu(:new.id)")]
    // comments in the header (one says BEGIN and DISABLE) stay where they are; DISABLE goes before them
    [InlineData(
        "CREATE TRIGGER t5 /* BEGIN */ BEFORE INSERT ON kunde -- DISABLE?\nFOR EACH ROW -- each row\nBEGIN NULL; END;",
        "CREATE TRIGGER t5 /* BEGIN */ BEFORE INSERT ON kunde -- DISABLE?\nFOR EACH ROW DISABLE -- each row\nBEGIN NULL; END;")]
    public void Disable_goes_where_oracle_expects_it(string sql, string expected) =>
        Assert.Equal(expected, TriggerDdl.WithDisable(sql));

    [Theory]
    [InlineData("CREATE TRIGGER t1 BEFORE INSERT ON kunde FOR EACH ROW ENABLE BEGIN NULL; END;", true)]
    [InlineData("CREATE TRIGGER t1 BEFORE INSERT ON kunde FOR EACH ROW disable WHEN (new.id > 0) BEGIN NULL; END;", false)]
    public void A_trigger_that_says_enable_or_disable_itself_stays_as_written(string sql, bool enabled)
    {
        Assert.Null(TriggerDdl.WithDisable(sql));
        Assert.Equal(enabled, TriggerDdl.StatedEnabled(sql));
    }

    [Theory]
    [InlineData("CREATE TABLE t (id NUMBER)")]
    [InlineData("CREATE OR REPLACE PROCEDURE p AS BEGIN NULL; END;")]
    [InlineData("CREATE OR REPLACE TRIGGER logon_trg AFTER LOGON ON DATABASE BEGIN NULL; END;")]
    [InlineData("ALTER TRIGGER t1 ENABLE")]
    [InlineData("CREATE TRIGGER t1 BEFORE INSERT ON kunde")] // no body: nothing to put DISABLE before
    public void Anything_else_is_left_alone(string sql)
    {
        Assert.Null(TriggerDdl.WithDisable(sql));
        Assert.Null(TriggerDdl.StatedEnabled(sql));
    }

    [Fact]
    public void Enable_and_disable_name_the_trigger_with_its_owner()
    {
        Assert.Equal("ALTER TRIGGER \"APP\".\"KUNDE_AUD\" ENABLE", TriggerDdl.Enable("APP", "KUNDE_AUD"));
        Assert.Equal("ALTER TRIGGER \"APP\".\"Kunde_Aud\" DISABLE", TriggerDdl.Disable("APP", "Kunde_Aud"));
        Assert.True(FerretSharp.Core.Oracle.StatementGuard.IsDdl(TriggerDdl.Disable("APP", "KUNDE_AUD")));
    }

    [Fact]
    public void Text_inside_the_body_does_not_count_as_the_header()
    {
        const string sql = "CREATE TRIGGER t1 BEFORE INSERT ON kunde FOR EACH ROW BEGIN EXECUTE IMMEDIATE 'ALTER TRIGGER x ENABLE'; END;";

        Assert.Null(TriggerDdl.StatedEnabled(sql));
        Assert.Contains("FOR EACH ROW DISABLE BEGIN", TriggerDdl.WithDisable(sql), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("CREATE TRIGGER kunde_aud AFTER INSERT ON kunde BEGIN NULL; END;", null, "KUNDE_AUD")]
    [InlineData("create or replace editionable trigger app.Kunde_Aud after insert on kunde begin null; end;", "APP", "KUNDE_AUD")]
    [InlineData("""CREATE OR REPLACE TRIGGER "App"."Kunde Aud" AFTER INSERT ON kunde BEGIN NULL; END;""", "App", "Kunde Aud")]
    [InlineData("""CREATE TRIGGER app."Mixed" AFTER INSERT ON kunde BEGIN NULL; END;""", "APP", "Mixed")]
    [InlineData("CREATE TRIGGER IF NOT EXISTS t1 AFTER INSERT ON kunde BEGIN NULL; END;", null, "T1")]
    [InlineData("-- audit\nCREATE /* new */ TRIGGER t1 AFTER INSERT ON kunde BEGIN NULL; END;", null, "T1")]
    public void The_name_comes_in_dictionary_form(string sql, string? owner, string name) =>
        Assert.Equal((owner, name), TriggerDdl.NameOf(sql));

    [Theory]
    [InlineData("CREATE TABLE t (id NUMBER)")]
    [InlineData("ALTER TRIGGER t1 ENABLE")]
    [InlineData("CREATE TRIGGER ddl_trg BEFORE DDL ON SCHEMA BEGIN NULL; END;")]
    [InlineData("")]
    public void Other_statements_name_no_trigger(string sql) => Assert.Null(TriggerDdl.NameOf(sql));

    /// <summary>Oracle counts a trigger's error lines from its body (BEGIN/DECLARE is line 1, Oracle 23).</summary>
    [Fact]
    public void A_compile_error_is_found_from_the_body_on()
    {
        const string sql = "CREATE TRIGGER t\n  BEFORE INSERT ON k\n  FOR EACH ROW WHEN (NEW.ID > 0)\nDECLARE\n  x NUMBER;\nBEGIN\n  :new.falsch := 1;\nEND;";

        Assert.Equal(sql.IndexOf(":new.falsch", StringComparison.Ordinal), TriggerDdl.ErrorOffset(sql, 4, 3));
        Assert.Equal(sql.IndexOf("DECLARE", StringComparison.Ordinal), TriggerDdl.ErrorOffset(sql, 1, 1));
        Assert.Equal("CREATE TRIGGER t BEFORE INSERT ON k FOR EACH ROW BEGIN NULL; END;".IndexOf("NULL", StringComparison.Ordinal),
            TriggerDdl.ErrorOffset("CREATE TRIGGER t BEFORE INSERT ON k FOR EACH ROW BEGIN NULL; END;", 1, 7));
        Assert.Null(TriggerDdl.ErrorOffset(sql, 20, 1));
        Assert.Null(TriggerDdl.ErrorOffset("CREATE TABLE t (x NUMBER)", 1, 1));
    }

    [Fact]
    public void Enable_names_the_trigger_quoted_with_its_owner() =>
        Assert.Equal("""ALTER TRIGGER "APP"."Kunde Aud" ENABLE""", TriggerDdl.Enable("APP", "Kunde Aud"));

    [Fact]
    public void The_compile_error_lists_the_first_errors_and_says_the_trigger_stays_off()
    {
        PlSqlError Error(int line) => new(PlSqlPart.Spec, line, 5, $"PLS-00201: Bezeichner 'X{line}' muss deklariert werden", false);
        var inner = new DatabaseException("ORA-24344: success with compilation error", "ORA-24344");

        var four = new TriggerCompileException("APP", "KUNDE_AUD", [Error(2), Error(3), Error(4), Error(5)], isEnabled: false, null, inner);
        var none = new TriggerCompileException("APP", "KUNDE_AUD", [], isEnabled: true, null, inner);

        Assert.Equal(
            "Trigger APP.KUNDE_AUD wurde angelegt, hat aber Kompilierfehler – er bleibt ausgeschaltet, DML auf seiner Tabelle läuft weiter. "
            + "2:5 PLS-00201: Bezeichner 'X2' muss deklariert werden; 3:5 PLS-00201: Bezeichner 'X3' muss deklariert werden; "
            + "4:5 PLS-00201: Bezeichner 'X4' muss deklariert werden (1 weiterer Fehler)",
            four.Message);
        Assert.Equal("ORA-24344", four.ErrorCode);
        Assert.Equal(4, four.Errors.Count);
        Assert.StartsWith("Trigger APP.KUNDE_AUD hat Kompilierfehler und ist eingeschaltet", none.Message, StringComparison.Ordinal);
        Assert.EndsWith("Seine Fehler ließen sich nicht lesen (ALL_ERRORS).", none.Message, StringComparison.Ordinal);
        using (UiCulture.Use("en"))
        {
            Assert.StartsWith("Trigger APP.KUNDE_AUD was created but has compile errors – it stays disabled",
                new TriggerCompileException("APP", "KUNDE_AUD", [Error(2)], false, null, inner).Message, StringComparison.Ordinal);
        }
    }

    /// <summary>With the statement the message names its lines, as the SQL editor numbers them – not Oracle's count from BEGIN.</summary>
    [Fact]
    public void The_compile_error_names_the_lines_of_the_statement()
    {
        const string sql = "CREATE TRIGGER t\n  BEFORE INSERT ON k\n  FOR EACH ROW DISABLE\nBEGIN\n  :new.falsch := 1;\nEND;";
        var error = new PlSqlError(PlSqlPart.Spec, 2, 3, "PLS-00049: Ungültige Bind-Variable 'NEW.FALSCH'", false);

        var exception = new TriggerCompileException("APP", "T", [error], false, new QuerySpec(sql, []));

        Assert.EndsWith("5:3 PLS-00049: Ungültige Bind-Variable 'NEW.FALSCH'", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// R5 review: with the body on the header's line, the DISABLE FerretSharp put in shifts the columns of the text that ran
    /// – the message counts in the user's text, where the SQL editor marks the error.
    /// </summary>
    [Fact]
    public void The_compile_error_column_is_the_users_when_the_body_shares_the_header_line()
    {
        const string written = "CREATE TRIGGER t BEFORE INSERT ON k FOR EACH ROW BEGIN :new.falsch := 1; END;";
        var ran = TriggerDdl.WithDisable(written)!;
        var error = new PlSqlError(PlSqlPart.Spec, 1, 7, "PLS-00049: Ungültige Bind-Variable 'NEW.FALSCH'", false);

        var exception = new TriggerCompileException("APP", "T", [error], false, new QuerySpec(ran, []), source: written);

        Assert.Contains($"1:{written.IndexOf(":new", StringComparison.Ordinal) + 1} ", exception.Message, StringComparison.Ordinal);
        Assert.Equal(ran, exception.Statement?.Sql); // Details show what ran
    }

    [Theory]
    [InlineData("CREATE TRIGGER IF NOT EXISTS t BEFORE INSERT ON k BEGIN NULL; END;", true)]
    [InlineData("create or replace trigger if not exists app.t before insert on k begin null; end;", true)]
    [InlineData("CREATE TRIGGER t BEFORE INSERT ON k BEGIN NULL; END;", false)]
    [InlineData("CREATE TABLE IF NOT EXISTS t (x NUMBER)", false)]
    public void If_not_exists_is_recognised(string sql, bool expected) => Assert.Equal(expected, TriggerDdl.IfNotExists(sql));
}
