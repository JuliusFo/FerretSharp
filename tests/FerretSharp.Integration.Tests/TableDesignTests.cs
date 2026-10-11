using FerretSharp.Core.Connections;
using FerretSharp.Core.Data;
using FerretSharp.Core.Design;
using FerretSharp.Core.Oracle;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;
using Oracle.ManagedDataAccess.Client;

namespace FerretSharp.Integration.Tests;

/// <summary>
/// DDL of the table designer (WP-23) run through the schema path against Oracle: what the designer writes runs, and what
/// it refuses beforehand is what Oracle refuses.
/// </summary>
public sealed class TableDesignTests(OracleContainerFixture oracle) : IAsyncLifetime
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

            await using var connection = new OracleConnection(oracle.RequireConnectionString());
            await connection.OpenAsync(Ct);
            foreach (var sql in new[]
            {
                "CREATE TABLE TD_KUNDE (ID NUMBER(10) PRIMARY KEY, NAME VARCHAR2(50 CHAR) NOT NULL)",
                "CREATE TABLE TD_AUFTRAG (ID NUMBER(10) PRIMARY KEY, KUNDE_ID NUMBER(10) CONSTRAINT FK_TD_AUFTRAG_KUNDE REFERENCES TD_KUNDE, "
                + "\"Nummer\" VARCHAR2(20), DATUM DATE, NOTIZ CLOB)",
                "INSERT INTO TD_KUNDE VALUES (1, 'Eins')",
                "INSERT INTO TD_AUFTRAG VALUES (1, 1, 'A-1', DATE '2026-10-01', NULL)",
                "INSERT INTO TD_AUFTRAG VALUES (2, 1, 'A-1', DATE '2026-10-02', NULL)",
                "COMMIT",
            })
            {
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                await command.ExecuteNonQueryAsync(Ct);
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

    private TableRef Ref(string table) => new(oracle.RequireProfile().Profile.EffectiveSchema, table);

    private async Task<(TableDetails Details, IReadOnlyList<IndexInfo> Indexes)> ReadAsync(IDatabaseConnection connection, string table)
    {
        var details = await connection.Schema.GetDetailsAsync(new TableSummary(Ref(table).Owner, table, TableKind.Table), Ct);
        return (details, await connection.Schema.GetIndexesAsync(Ref(table), Ct));
    }

    [Fact]
    public async Task Index_for_an_unindexed_foreign_key_runs_and_covers_it()
    {
        await using var connection = await OpenAsync("Design fk index");
        var (details, indexes) = await ReadAsync(connection, "TD_AUFTRAG");
        var foreignKeys = (await connection.Schema.GetForeignKeysAsync(Ref("TD_AUFTRAG").Owner, Ct)).Where(f => f.From == Ref("TD_AUFTRAG")).ToList();
        var fk = Assert.Single(IndexAdvice.UnindexedForeignKeys(foreignKeys, indexes));

        var draft = IndexDesign.ForForeignKey(fk, indexes);
        Assert.True(IndexDesign.Check(draft, details, indexes).CanRun);
        await connection.Editor.ExecuteDdlAsync(IndexDesign.Ddl(draft, Ref("TD_AUFTRAG")), Ct);

        var after = await connection.Schema.GetIndexesAsync(Ref("TD_AUFTRAG"), Ct);
        var created = Assert.Single(after, i => i.Name == "IX_TD_AUFTRAG_KUNDE_ID");
        Assert.False(created.Unique);
        Assert.Empty(IndexAdvice.UnindexedForeignKeys(foreignKeys, after));
        Assert.Equal(TransactionMode.None, connection.Editor.Transaction.Mode);

        // The designer refuses the same column list again – as Oracle does.
        var again = new IndexDraft("IX_TD_AUFTRAG_KUNDE_ID_2", ["KUNDE_ID"]);
        Assert.False(IndexDesign.Check(again, details, after).CanRun);
        var error = await Assert.ThrowsAsync<DatabaseException>(() => connection.Editor.ExecuteDdlAsync(IndexDesign.Ddl(again, Ref("TD_AUFTRAG")), Ct));
        Assert.Equal("ORA-01408", error.ErrorCode);

        await connection.Editor.ExecuteDdlAsync($"DROP INDEX \"{created.Owner}\".\"{created.Name}\"", Ct);
    }

    [Fact]
    public async Task Unique_index_on_a_mixed_case_column_runs_and_unique_on_duplicates_fails()
    {
        await using var connection = await OpenAsync("Design unique index");
        var (details, indexes) = await ReadAsync(connection, "TD_AUFTRAG");

        var unique = new IndexDraft(IndexDesign.SuggestName("TD_AUFTRAG", ["Nummer", "DATUM"], indexes.Select(i => i.Name)), ["Nummer", "DATUM"], Unique: true);
        Assert.Equal("IX_TD_AUFTRAG_NUMMER_DATUM", unique.Name); // a plain name, though the column is "Nummer"
        var check = IndexDesign.Check(unique, details, indexes);
        Assert.True(check.CanRun);
        Assert.Empty(check.Warnings);
        await connection.Editor.ExecuteDdlAsync(IndexDesign.Ddl(unique, Ref("TD_AUFTRAG")), Ct);
        var created = Assert.Single(await connection.Schema.GetIndexesAsync(Ref("TD_AUFTRAG"), Ct), i => i.Name == unique.Name);
        Assert.True(created.Unique);
        Assert.Equal(["Nummer", "DATUM"], created.Columns.Select(c => c.Name));
        await connection.Editor.ExecuteDdlAsync($"DROP INDEX \"{created.Owner}\".\"{created.Name}\"", Ct);

        // Duplicates are Oracle's to find; nothing is left behind.
        var duplicates = new IndexDraft("UX_TD_AUFTRAG_NUMMER", ["Nummer"], Unique: true);
        var error = await Assert.ThrowsAsync<DatabaseException>(() => connection.Editor.ExecuteDdlAsync(IndexDesign.Ddl(duplicates, Ref("TD_AUFTRAG")), Ct));
        Assert.Equal("ORA-01452", error.ErrorCode);
        Assert.DoesNotContain(await connection.Schema.GetIndexesAsync(Ref("TD_AUFTRAG"), Ct), i => i.Name == duplicates.Name);
    }

    /// <summary>
    /// CREATE INDEX is no ALTER TABLE: the schema path does not take the table first. With another session's uncommitted
    /// DML it fails at once with ORA-00054 (WP-22) – the designer shows the lock holders as the SQL editor does.
    /// </summary>
    [Fact]
    public async Task Index_on_a_table_another_session_has_written_to_fails_at_once()
    {
        await using var writer = await OpenAsync("Design busy writer");
        await using var connection = await OpenAsync("Design busy ddl");
        await writer.Editor.ExecuteAsync(new QuerySpec("UPDATE td_auftrag SET datum = datum WHERE id = 1", []), Ct);
        var draft = new IndexDraft("IX_TD_AUFTRAG_DATUM", ["DATUM"]);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<DatabaseException>(() => connection.Editor.ExecuteDdlAsync(IndexDesign.Ddl(draft, Ref("TD_AUFTRAG")), Ct));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Took {stopwatch.Elapsed}.");
        Assert.Equal("ORA-00054", error.ErrorCode);

        await writer.Editor.RollbackAsync(Ct);
        await connection.Editor.ExecuteDdlAsync(IndexDesign.Ddl(draft, Ref("TD_AUFTRAG")), Ct);
        await connection.Editor.ExecuteDdlAsync($"DROP INDEX \"{Ref("TD_AUFTRAG").Owner}\".\"IX_TD_AUFTRAG_DATUM\"", Ct);
    }
}
