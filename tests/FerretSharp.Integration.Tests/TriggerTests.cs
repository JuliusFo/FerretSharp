using System.Diagnostics;
using FerretSharp.Core.Connections;
using FerretSharp.Core.Data;
using FerretSharp.Core.Ddl;
using FerretSharp.Core.Oracle;
using FerretSharp.Core.Query;
using Oracle.ManagedDataAccess.Client;

namespace FerretSharp.Integration.Tests;

/// <summary>
/// Triggers through the schema path (WP-26, ADR 0021): created with DISABLE, checked for compile errors, enabled only if
/// valid – so an invalid trigger never stops DML on its table (ORA-04098 for every user).
/// </summary>
public sealed class TriggerTests(OracleContainerFixture oracle) : IAsyncLifetime
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static bool _created;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await Gate.WaitAsync(Ct);
        try
        {
            if (_created)
            {
                return;
            }

            foreach (var sql in new[]
            {
                "CREATE TABLE TRG_KUNDE (ID NUMBER(10) PRIMARY KEY, NAME VARCHAR2(50 CHAR))",
                "CREATE TABLE TRG_KUNDE_AUD (AUD_ID NUMBER GENERATED ALWAYS AS IDENTITY, OP CHAR(1) NOT NULL, ID NUMBER(10), OLD_NAME VARCHAR2(50 CHAR), NEW_NAME VARCHAR2(50 CHAR))",
                "CREATE TABLE TRG_FEHLER (ID NUMBER(10) PRIMARY KEY, NAME VARCHAR2(50 CHAR))",
                "CREATE TABLE TRG_REST (ID NUMBER(10))",
                "CREATE TABLE TRG_ANDERE (ID NUMBER(10))",
                "CREATE TABLE TRG_ROH (ID NUMBER(10))",
                "CREATE TABLE TRG_SPERRE (ID NUMBER(10))",
            })
            {
                await RawAsync(sql);
            }

            _created = true;
        }
        finally
        {
            Gate.Release();
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<IDatabaseConnection> OpenAsync(string action)
    {
        var (profile, password) = oracle.RequireProfile();
        return await new OracleDatabaseConnector().OpenAsync(profile, password, action, Ct);
    }

    /// <summary>A statement on a plain connection, past every guard of FerretSharp.</summary>
    private async Task RawAsync(string sql)
    {
        await using var connection = new OracleConnection(oracle.RequireConnectionString());
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Ct);
    }

    private static async Task<object?> ScalarAsync(IDatabaseConnection connection, string sql) =>
        (await connection.Data.ReadSqlAsync(new QuerySpec(sql, []), 0, 1, Ct)).Rows.SingleOrDefault()?[0];

    /// <summary>STATUS of USER_TRIGGERS (ENABLED/DISABLED) and of USER_OBJECTS (VALID/INVALID).</summary>
    private static async Task<(object? Trigger, object? Object)> StatusAsync(IDatabaseConnection connection, string name) =>
        (await ScalarAsync(connection, $"SELECT status FROM user_triggers WHERE trigger_name = '{name}'"),
         await ScalarAsync(connection, $"SELECT status FROM user_objects WHERE object_name = '{name}' AND object_type = 'TRIGGER'"));

    private const string AuditScript = """
        CREATE OR REPLACE TRIGGER trg_kunde_aud
          AFTER INSERT OR UPDATE OR DELETE ON trg_kunde
          FOR EACH ROW
        DECLARE
          v_op CHAR(1);
        BEGIN
          <<entscheiden>>
          IF INSERTING THEN
            v_op := 'I';
            GOTO schreiben;
          END IF;
          IF UPDATING THEN v_op := 'U'; ELSE v_op := 'D'; END IF;
          <<schreiben>>
          INSERT INTO trg_kunde_aud (op, id, old_name, new_name) VALUES (v_op, NVL(:new.id, :old.id), :old.name, :new.name);
        END;
        /
        COMMENT ON TABLE trg_kunde_aud IS 'Audit; per Trigger'
        """;

    /// <summary>a) An audit trigger as the SQL editor runs it (split at the "/" line): enabled, valid and writing rows.</summary>
    [Fact]
    public async Task An_audit_trigger_ends_enabled_and_valid_and_writes_audit_rows()
    {
        await using var connection = await OpenAsync("TRG audit");
        var script = SqlScriptPlan.Prepare(SqlScript.Split(AuditScript), [], writable: true, single: false);
        Assert.Null(script.Problem);
        Assert.Equal(2, script.Statements.Count);

        foreach (var statement in script.Statements)
        {
            Assert.True(statement.Info.IsDdl);
            await connection.Editor.ExecuteDdlAsync(statement.Query.Sql, Ct);
        }

        Assert.Equal(("ENABLED", "VALID"), await StatusAsync(connection, "TRG_KUNDE_AUD"));
        foreach (var dml in new[]
        {
            "INSERT INTO trg_kunde VALUES (1, 'Erste')",
            "UPDATE trg_kunde SET name = 'Neu' WHERE id = 1",
            "DELETE FROM trg_kunde WHERE id = 1",
        })
        {
            await connection.Editor.ExecuteAsync(new QuerySpec(dml, []), Ct);
        }

        var audit = await connection.Data.ReadSqlAsync(
            new QuerySpec("SELECT op, id, old_name, new_name FROM trg_kunde_aud ORDER BY aud_id", []), 0, 10, Ct);
        Assert.Equal(
            [["I", 1m, null, "Erste"], ["U", 1m, "Erste", "Neu"], ["D", 1m, "Neu", null]],
            audit.Rows.Select(r => r.ToArray()));
        Assert.Equal("Audit; per Trigger", await ScalarAsync(connection, "SELECT comments FROM user_tab_comments WHERE table_name = 'TRG_KUNDE_AUD'"));
        await connection.Editor.RollbackAsync(Ct);
    }

    /// <summary>
    /// ODP.NET reports CREATE TRIGGER with compile errors as success – no ORA-24344, unlike a package body (WP-28); the
    /// trigger exists, invalid and (without DISABLE) enabled. Only its status tells, which is why the schema path checks it.
    /// </summary>
    [Fact]
    public async Task Odp_reports_a_trigger_with_compile_errors_as_success()
    {
        await using var connection = await OpenAsync("TRG raw");

        await RawAsync("CREATE OR REPLACE TRIGGER trg_roh_kaputt BEFORE INSERT ON trg_roh FOR EACH ROW BEGIN :new.gibt_es_nicht := 1; END;");

        Assert.Equal(("ENABLED", "INVALID"), await StatusAsync(connection, "TRG_ROH_KAPUTT"));
        var error = await Assert.ThrowsAsync<DatabaseException>(() => connection.Editor.ExecuteAsync(new QuerySpec("INSERT INTO trg_roh VALUES (1)", []), Ct));
        Assert.Equal("ORA-04098", error.ErrorCode); // the table is blocked for everyone
        await connection.Editor.RollbackAsync(Ct);
        await RawAsync("DROP TRIGGER trg_roh_kaputt");
    }

    /// <summary>b) A trigger with a compile error: reported with its errors, created but disabled – DML on its table still works.</summary>
    [Fact]
    public async Task A_trigger_with_a_compile_error_stays_disabled_and_dml_still_works()
    {
        await using var connection = await OpenAsync("TRG error");
        const string sql = """
            CREATE OR REPLACE TRIGGER trg_fehler_biu
              BEFORE INSERT OR UPDATE ON trg_fehler
              FOR EACH ROW
            BEGIN
              :new.gibt_es_nicht := UPPER(:new.name);
            END;
            """;

        var error = await Assert.ThrowsAsync<TriggerCompileException>(() => connection.Editor.ExecuteDdlAsync(sql, Ct));

        var (profile, _) = oracle.RequireProfile();
        Assert.Equal((profile.EffectiveSchema, "TRG_FEHLER_BIU", false), (error.Owner, error.Name, error.IsEnabled));
        Assert.NotEmpty(error.Errors);
        var first = error.Errors[0];
        Assert.Contains("GIBT_ES_NICHT", first.Text, StringComparison.OrdinalIgnoreCase);

        // Oracle counts the lines from the body (BEGIN is line 1): the marker lands on ":new.gibt_es_nicht" all the same.
        Assert.Equal(2, first.Line);
        Assert.Equal(sql.IndexOf(":new.gibt_es_nicht", StringComparison.Ordinal), TriggerDdl.ErrorOffset(sql, first.Line, first.Column));
        Assert.Equal("ORA-24344", error.ErrorCode);
        Assert.Null(error.InnerException); // ODP.NET raised nothing: the status told
        Assert.Contains("FOR EACH ROW DISABLE", error.Statement?.Sql, StringComparison.Ordinal);
        Assert.Contains("5:3 ", error.Message, StringComparison.Ordinal); // the statement's own line, not Oracle's count from BEGIN
        Assert.StartsWith($"Trigger {error.Owner}.TRG_FEHLER_BIU wurde angelegt, hat aber Kompilierfehler", error.Message, StringComparison.Ordinal);
        Assert.Equal(("DISABLED", "INVALID"), await StatusAsync(connection, "TRG_FEHLER_BIU"));

        await connection.Editor.ExecuteAsync(new QuerySpec("INSERT INTO trg_fehler VALUES (1, 'geht')", []), Ct);
        Assert.Equal(1m, await ScalarAsync(connection, "SELECT COUNT(*) FROM trg_fehler"));
        await connection.Editor.RollbackAsync(Ct);
        await connection.Editor.ExecuteDdlAsync("DROP TRIGGER trg_fehler_biu", Ct);
    }

    /// <summary>
    /// c) Text behind the trigger's END is part of the trigger, never a statement of its own: Oracle compiles it as the
    /// body (a compile error), the table named there stays, the trigger stays disabled.
    /// </summary>
    [Fact]
    public async Task Text_behind_the_end_of_a_trigger_never_runs()
    {
        await using var connection = await OpenAsync("TRG tail");
        const string sql = "CREATE OR REPLACE TRIGGER trg_rest_bi BEFORE INSERT ON trg_rest FOR EACH ROW BEGIN NULL; END; DROP TABLE trg_andere PURGE";
        Assert.Single(SqlScript.Split(sql)); // the editor runs it as one statement as well

        var error = await Assert.ThrowsAsync<TriggerCompileException>(() => connection.Editor.ExecuteDdlAsync(sql, Ct));

        Assert.NotEmpty(error.Errors);
        Assert.Equal(1m, await ScalarAsync(connection, "SELECT COUNT(*) FROM user_tables WHERE table_name = 'TRG_ANDERE'"));
        Assert.Equal(("DISABLED", "INVALID"), await StatusAsync(connection, "TRG_REST_BI"));
        await connection.Editor.ExecuteAsync(new QuerySpec("INSERT INTO trg_rest VALUES (1)", []), Ct);
        await connection.Editor.RollbackAsync(Ct);
        await connection.Editor.ExecuteDdlAsync("DROP TRIGGER trg_rest_bi", Ct);
    }

    /// <summary>
    /// d) CREATE OR REPLACE TRIGGER, ALTER TRIGGER ENABLE/DISABLE and DROP TRIGGER on a table another session has written to
    /// and not committed neither wait nor fail (Oracle 23) – unlike ALTER TABLE … ADD, which waits and cannot be cancelled
    /// (ADR 0019). So the schema path takes no table lock first. The writer rolls back after 5 s, so the test never hangs.
    /// </summary>
    [Fact]
    public async Task Creating_a_trigger_does_not_wait_for_another_sessions_transaction()
    {
        await using var writer = await OpenAsync("TRG lock writer");
        await using var ddl = await OpenAsync("TRG lock ddl");
        await writer.Editor.ExecuteAsync(new QuerySpec("INSERT INTO trg_sperre VALUES (1)", []), Ct);
        using var release = new CancellationTokenSource();
        var released = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), release.Token);
            }
            catch (OperationCanceledException)
            {
                // the DDL did not wait
            }

            await writer.Editor.RollbackAsync(CancellationToken.None);
        }, Ct);

        var stopwatch = Stopwatch.StartNew();
        await ddl.Editor.ExecuteDdlAsync("CREATE OR REPLACE TRIGGER trg_sperre_bi BEFORE INSERT ON trg_sperre FOR EACH ROW BEGIN :new.id := -:new.id; END;", Ct);
        var created = stopwatch.Elapsed;
        await ddl.Editor.ExecuteDdlAsync("CREATE OR REPLACE TRIGGER trg_sperre_bi BEFORE INSERT ON trg_sperre FOR EACH ROW BEGIN :new.id := -:new.id * 10; END;", Ct);
        var replaced = stopwatch.Elapsed - created;
        var stillOpen = writer.Editor.Transaction.Mode == TransactionMode.ReadWrite;

        // The writer's transaction is still open, and its next INSERT already fires the new trigger.
        await writer.Editor.ExecuteAsync(new QuerySpec("INSERT INTO trg_sperre VALUES (2)", []), Ct);
        var fired = await ScalarAsync(writer, "SELECT MIN(id) FROM trg_sperre");
        await release.CancelAsync();
        await released;

        Assert.True(stillOpen, "The writer's transaction ended before the DDL – the test proves nothing.");
        Assert.True(created < TimeSpan.FromSeconds(3), $"CREATE took {created}.");
        Assert.True(replaced < TimeSpan.FromSeconds(3), $"CREATE OR REPLACE took {replaced}.");
        Assert.Equal(-20m, fired);
        Assert.Equal(("ENABLED", "VALID"), await StatusAsync(ddl, "TRG_SPERRE_BI"));
        await ddl.Editor.ExecuteDdlAsync("DROP TRIGGER trg_sperre_bi", Ct);
    }
}
