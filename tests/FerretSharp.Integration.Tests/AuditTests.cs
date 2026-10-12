using FerretSharp.Core.Audit;
using FerretSharp.Core.Connections;
using FerretSharp.Core.Design;
using FerretSharp.Core.Oracle;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;
using Oracle.ManagedDataAccess.Client;

namespace FerretSharp.Integration.Tests;

/// <summary>
/// WP-26 end to end: template → loader → planner → schema path. The audit table and trigger are created, write rows,
/// follow a new column and a dropped one, and a second plan is empty.
/// </summary>
public sealed class AuditTests(OracleContainerFixture oracle) : IAsyncLifetime
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static bool _created;

    /// <summary>The pattern of the user's company (neutral names), as in the template tests of Core.Tests.</summary>
    private const string CompanyTemplate = """
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
                "CREATE TABLE AUD_KUNDE (KUNDE_ID NUMBER(10) PRIMARY KEY, NAME VARCHAR2(80 CHAR), NOTIZ CLOB, STATUS CHAR(1) NOT NULL)",
                "COMMENT ON COLUMN AUD_KUNDE.NAME IS 'Name of the customer'",
                "CREATE SEQUENCE AUDIT_ID_SEQ",
                "CREATE TABLE AUD_LIEFERANT (LIEFERANT_ID NUMBER(10) PRIMARY KEY, NAME VARCHAR2(80 CHAR), NOTIZ CLOB, \"Anhang\" BLOB)",
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

    private async Task RawAsync(string sql)
    {
        await using var connection = new OracleConnection(oracle.RequireConnectionString());
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Ct);
    }

    private async Task<(IDatabaseConnection Connection, SchemaCache Schema)> OpenAsync(string action)
    {
        var (profile, password) = oracle.RequireProfile();
        var connection = await new OracleDatabaseConnector().OpenAsync(profile, password, action, Ct);
        var schema = new SchemaCache(connection.Schema, profile.EffectiveSchema);
        await schema.LoadAsync(Ct);
        return (connection, schema);
    }

    /// <summary>Loads what the schema has, plans and runs every step through the schema path; returns the plan.</summary>
    private static async Task<DesignPlan> SyncAsync(IDatabaseConnection connection, SchemaCache schema, AuditTemplate template, string table)
    {
        await schema.RefreshAsync(Ct);
        var summary = schema.Find(new TableRef(schema.Owner, table))!;
        var details = await schema.GetDetailsAsync(summary, Ct);
        var state = await AuditLoader.LoadAsync(schema, template.NamesFor(summary.Ref), Ct);
        var facts = state.AuditTable is { } audit
            ? await TableDesign.CountAsync(connection.Data, audit, AuditPlanner.Questions(template, summary.Ref, details.Columns, state), Ct)
            : DataFacts.None;
        var plan = AuditPlanner.Plan(template, summary.Ref, details.Columns, state, facts, taken: schema.Tables.Select(t => t.Name));
        Assert.Empty(plan.Problems);
        foreach (var step in plan.Steps)
        {
            await connection.Editor.ExecuteDdlAsync(step.Sql, Ct);
        }

        return plan;
    }

    private static async Task<IReadOnlyList<object?[]>> RowsAsync(IDatabaseConnection connection, string sql) =>
        (await connection.Data.ReadSqlAsync(new QuerySpec(sql, []), 0, 100, Ct)).Rows.Select(r => r.ToArray()).ToList();

    private static async Task DmlAsync(IDatabaseConnection connection, params string[] statements)
    {
        foreach (var dml in statements)
        {
            await connection.Editor.ExecuteAsync(new QuerySpec(dml, []), Ct);
        }

        await connection.Editor.CommitAsync(Ct);
    }

    [Fact]
    public async Task The_company_template_audits_and_follows_new_and_dropped_columns()
    {
        var (connection, schema) = await OpenAsync("AUD company");
        await using var _ = connection;
        var template = AuditTemplates.Parse(CompanyTemplate);

        var created = await SyncAsync(connection, schema, template, "AUD_KUNDE");
        Assert.Contains(created.Steps, s => s.Kind == Core.Ddl.DdlStepKind.CreateTable);
        Assert.Equal([["ENABLED", "VALID"]], await RowsAsync(connection,
            "SELECT t.status, o.status FROM user_triggers t JOIN user_objects o ON o.object_name = t.trigger_name AND o.object_type = 'TRIGGER' WHERE t.trigger_name = 'AUD_KUNDE_AUDIT'"));
        Assert.Equal([["Name of the customer"]], await RowsAsync(connection,
            "SELECT comments FROM user_col_comments WHERE table_name = 'AUDIT_AUD_KUNDE' AND column_name = 'NAME'"));

        await DmlAsync(connection,
            "INSERT INTO aud_kunde (kunde_id, name, status) VALUES (1, 'Erste', 'A')",
            "UPDATE aud_kunde SET name = 'Erste' WHERE kunde_id = 1", // no value changed: no audit row
            "UPDATE aud_kunde SET name = NULL WHERE kunde_id = 1",
            "DELETE FROM aud_kunde WHERE kunde_id = 1");
        Assert.Equal([["I", "Erste", "A"], ["U", null, "A"], ["D", null, "A"]],
            await RowsAsync(connection, "SELECT audit_trans, name, status FROM audit_aud_kunde ORDER BY audit_id"));

        // A new column: added to the audit table, the trigger (still valid) rewritten to fill it.
        await connection.Editor.ExecuteDdlAsync("ALTER TABLE aud_kunde ADD (fax VARCHAR2(30 CHAR))", Ct);
        var added = await SyncAsync(connection, schema, template, "AUD_KUNDE");
        Assert.Equal([Core.Ddl.DdlStepKind.AddColumn, Core.Ddl.DdlStepKind.PlSqlObject], added.Steps.Select(s => s.Kind));
        await DmlAsync(connection, "INSERT INTO aud_kunde (kunde_id, name, status, fax) VALUES (2, 'Zweite', 'B', '0815')");
        Assert.Equal([["I", "0815"]], await RowsAsync(connection, "SELECT audit_trans, fax FROM audit_aud_kunde WHERE kunde_id = 2"));

        // A dropped column: it stays in the audit table (nullable), the now invalid trigger is rewritten.
        await connection.Editor.ExecuteDdlAsync("ALTER TABLE aud_kunde DROP COLUMN status", Ct);
        var dropped = await SyncAsync(connection, schema, template, "AUD_KUNDE");
        Assert.Contains(dropped.Steps, s => s.Kind == Core.Ddl.DdlStepKind.ModifyNull);
        Assert.Contains(dropped.Warnings, w => w.Contains("STATUS"));
        await DmlAsync(connection, "INSERT INTO aud_kunde (kunde_id, name) VALUES (3, 'Dritte')");
        Assert.Equal([["I", null, "Dritte"]], await RowsAsync(connection, "SELECT audit_trans, status, name FROM audit_aud_kunde WHERE kunde_id = 3"));

        Assert.Empty((await SyncAsync(connection, schema, template, "AUD_KUNDE")).Steps);
    }

    /// <summary>The default template: an identity instead of a sequence; a LOB that alone was set is audited (UPDATING), also with a mixed-case name.</summary>
    [Fact]
    public async Task The_default_template_needs_no_sequence_audits_lob_updates_and_a_second_plan_is_empty()
    {
        var (connection, schema) = await OpenAsync("AUD default");
        await using var _ = connection;

        var plan = await SyncAsync(connection, schema, AuditTemplates.Default, "AUD_LIEFERANT");
        Assert.Contains("UPDATING('Anhang')", plan.Steps[^1].Sql);
        await DmlAsync(connection,
            "INSERT INTO aud_lieferant (lieferant_id, name) VALUES (1, 'Erster')",
            "UPDATE aud_lieferant SET name = 'Neu' WHERE lieferant_id = 1",
            "UPDATE aud_lieferant SET name = 'Neu' WHERE lieferant_id = 1", // no value changed: no audit row
            "UPDATE aud_lieferant SET notiz = 'Text' WHERE lieferant_id = 1",
            "UPDATE aud_lieferant SET \"Anhang\" = HEXTORAW('CAFE') WHERE lieferant_id = 1",
            "DELETE FROM aud_lieferant WHERE lieferant_id = 1");

        Assert.Equal([["I", "Erster", null], ["U", "Neu", null], ["U", "Neu", "Text"], ["U", "Neu", "Text"], ["D", "Neu", "Text"]],
            await RowsAsync(connection, "SELECT audit_trans, name, TO_CHAR(notiz) FROM audit_aud_lieferant ORDER BY audit_id"));
        Assert.Empty((await SyncAsync(connection, schema, AuditTemplates.Default, "AUD_LIEFERANT")).Steps);
    }
}
