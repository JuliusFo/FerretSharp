using Oracle.ManagedDataAccess.Client;

namespace FerretSharp.Integration.Tests;

/// <summary>
/// How Oracle and ODP.NET (managed) behave with transactions – the facts the transaction model of v2 relies on.
/// Plain driver calls, no FerretSharp code.
/// </summary>
public sealed class TransactionBehaviorTests(OracleContainerFixture oracle) : IAsyncLifetime
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static bool _created;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await Gate.WaitAsync(Ct);
        try
        {
            if (!_created)
            {
                await using var connection = await OpenAsync();
                // With deferred segment creation (default), the first insert while a snapshot is open makes the
                // snapshot fail with ORA-08176 – FerretSharp restarts the snapshot then; the test avoids it.
                await ExecuteAsync(connection, null, "CREATE TABLE TX_TEST (ID NUMBER PRIMARY KEY, TXT VARCHAR2(20)) SEGMENT CREATION IMMEDIATE");
                _created = true;
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<OracleConnection> OpenAsync()
    {
        var connection = new OracleConnection(oracle.RequireConnectionString());
        await connection.OpenAsync(Ct);
        return connection;
    }

    private static async Task<int> ExecuteAsync(OracleConnection connection, OracleTransaction? transaction, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (transaction is not null)
        {
            command.Transaction = transaction;
        }

        return await command.ExecuteNonQueryAsync(Ct);
    }

    private static async Task<long> CountAsync(OracleConnection connection, string where)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM TX_TEST WHERE " + where;
        return Convert.ToInt64(await command.ExecuteScalarAsync(Ct));
    }

    [Fact]
    public async Task Set_transaction_read_only_works_as_first_statement_of_an_odp_transaction()
    {
        await using var connection = await OpenAsync();
        await using var transaction = connection.BeginTransaction();

        await ExecuteAsync(connection, transaction, "SET TRANSACTION READ ONLY");

        // A command without Transaction property runs in the same (read-only) transaction.
        var error = await Assert.ThrowsAsync<OracleException>(() => ExecuteAsync(connection, null, "INSERT INTO TX_TEST VALUES (1, 'ro')"));
        Assert.Equal(1456, error.Number); // may not perform insert/delete/update operation inside a READ ONLY transaction
        await transaction.RollbackAsync(Ct);
    }

    [Fact]
    public async Task Read_only_transaction_sees_the_data_of_its_start_until_it_ends()
    {
        await using var reader = await OpenAsync();
        await using var writer = await OpenAsync();
        var transaction = await BeginReadOnlyOnSettledTableAsync(reader);
        Assert.Equal(0, await CountAsync(reader, "ID = 2"));

        await ExecuteAsync(writer, null, "INSERT INTO TX_TEST VALUES (2, 'committed')"); // autocommit

        Assert.Equal(0, await CountAsync(reader, "ID = 2")); // still the snapshot
        await transaction.RollbackAsync(Ct);
        await transaction.DisposeAsync();
        Assert.Equal(1, await CountAsync(reader, "ID = 2")); // new statement, new data

        await ExecuteAsync(writer, null, "DELETE FROM TX_TEST WHERE ID = 2");
    }

    [Fact]
    public async Task Without_odp_transaction_set_transaction_read_only_does_not_protect()
    {
        await using var connection = await OpenAsync();

        // Autocommit: the statement starts a read-only transaction that ends right away.
        await ExecuteAsync(connection, null, "SET TRANSACTION READ ONLY");
        await ExecuteAsync(connection, null, "INSERT INTO TX_TEST VALUES (3, 'autocommit')");

        Assert.Equal(1, await CountAsync(connection, "ID = 3"));
        await ExecuteAsync(connection, null, "DELETE FROM TX_TEST WHERE ID = 3");
    }

    /// <summary>DDL commits implicitly and runs anyway – the statement guard of FerretSharp stays the protection against DDL.</summary>
    [Fact]
    public async Task Ddl_runs_despite_a_read_only_transaction()
    {
        await using var connection = await OpenAsync();
        await using var transaction = connection.BeginTransaction();
        await ExecuteAsync(connection, transaction, "SET TRANSACTION READ ONLY");

        await ExecuteAsync(connection, null, "CREATE TABLE TX_DDL_TEST (ID NUMBER)");

        Assert.Equal(1, await CountTablesAsync(connection, "TX_DDL_TEST"));
        await ExecuteAsync(connection, null, "DROP TABLE TX_DDL_TEST");
    }

    [Fact]
    public async Task Closing_a_connection_with_an_open_transaction_rolls_it_back()
    {
        await using (var connection = await OpenAsync())
        {
            var transaction = connection.BeginTransaction();
            await ExecuteAsync(connection, transaction, "INSERT INTO TX_TEST VALUES (4, 'open')");
        }

        await using var check = await OpenAsync();
        Assert.Equal(0, await CountAsync(check, "ID = 4"));
    }

    [Fact]
    public async Task Second_set_transaction_in_the_same_transaction_fails()
    {
        await using var connection = await OpenAsync();
        await using var transaction = connection.BeginTransaction();
        await ExecuteAsync(connection, transaction, "SET TRANSACTION READ ONLY");

        var error = await Assert.ThrowsAsync<OracleException>(() => ExecuteAsync(connection, transaction, "SET TRANSACTION READ ONLY"));
        Assert.Equal(1453, error.Number); // SET TRANSACTION must be first statement of transaction
        await transaction.RollbackAsync(Ct);
    }

    /// <summary>
    /// A snapshot older than the table's last DDL cannot read it (ORA-01466) – here right after CREATE TABLE in
    /// InitializeAsync. FerretSharp restarts the snapshot in that case; the test simply waits until the table settled.
    /// </summary>
    private static async Task<OracleTransaction> BeginReadOnlyOnSettledTableAsync(OracleConnection connection)
    {
        for (var attempt = 1; ; attempt++)
        {
            var transaction = connection.BeginTransaction();
            await ExecuteAsync(connection, transaction, "SET TRANSACTION READ ONLY");
            try
            {
                await CountAsync(connection, "1 = 0");
                return transaction;
            }
            catch (OracleException ex) when (ex.Number == 1466 && attempt < 10)
            {
                await transaction.RollbackAsync(Ct);
                await transaction.DisposeAsync();
                await Task.Delay(1000, Ct);
            }
        }
    }

    private static async Task<long> CountTablesAsync(OracleConnection connection, string name)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM user_tables WHERE table_name = :n";
        command.Parameters.Add(new OracleParameter("n", name));
        return Convert.ToInt64(await command.ExecuteScalarAsync(Ct));
    }
}
