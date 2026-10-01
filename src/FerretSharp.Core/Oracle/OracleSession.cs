using System.Data.Common;
using System.Diagnostics;
using FerretSharp.Core.Query;
using Oracle.ManagedDataAccess.Client;

namespace FerretSharp.Core.Oracle;

/// <summary>Values shown in <c>V$SESSION</c> (MODULE, ACTION, CLIENT_INFO) to identify who holds a session.</summary>
public sealed record SessionContext(string Module, string Action, string? ClientInfo = null);

/// <summary>
/// One long-lived Oracle connection (one per workspace). <see cref="OracleConnection"/> is not thread-safe,
/// so all commands are serialized. v1 is read-only: there is deliberately no ExecuteNonQuery.
/// </summary>
public sealed class OracleSession : IAsyncDisposable
{
    /// <summary>ORA-01013: user requested cancel of current operation.</summary>
    internal const int UserCancelledErrorNumber = 1013;

    /// <summary>
    /// Characters of LONG columns fetched with the row (<c>ALL_TAB_COLUMNS.DATA_DEFAULT</c>, <c>ALL_VIEWS.TEXT</c>).
    /// Without it, ODP.NET returns no LONG data. 32767 is the driver's maximum; table data never selects LONG columns.
    /// </summary>
    internal const int LongFetchSize = 32767;

    private readonly OracleConnection _connection;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private OracleSession(OracleConnection connection) => _connection = connection;

    public string ServerVersion => _connection.ServerVersion;

    public static async Task<OracleSession> OpenAsync(string connectionString, SessionContext context, CancellationToken cancellationToken)
    {
        var connection = new OracleConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);

            // ODP.NET derives NLS settings from the Windows locale; German Windows gives NLS_SORT=GERMAN, which sorts
            // digits after letters and cannot use B-tree indexes. Browsing needs a predictable, machine-independent order.
            var globalization = connection.GetSessionInfo();
            globalization.Sort = "BINARY";
            globalization.Comparison = "BINARY";
            connection.SetSessionInfo(globalization);

            connection.ModuleName = context.Module;
            connection.ActionName = context.Action;
            connection.ClientInfo = context.ClientInfo ?? string.Empty;
            return new OracleSession(connection);
        }
        catch (OracleException ex) when (ex.Number == UserCancelledErrorNumber)
        {
            await connection.DisposeAsync();
            throw new OperationCanceledException("Opening the session was cancelled.", ex, cancellationToken);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>Round trip with <c>SELECT 1 FROM DUAL</c>.</summary>
    public async Task<TimeSpan> PingAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        await ExecuteReaderAsync("SELECT 1 FROM DUAL", [], (reader, ct) => reader.ReadAsync(ct), cancellationToken);
        return stopwatch.Elapsed;
    }

    /// <summary>
    /// Runs a query and hands the open reader to <paramref name="read"/>. Cancelling the token cancels the
    /// statement on the server; the session stays usable afterwards.
    /// </summary>
    public async Task<T> ExecuteReaderAsync<T>(
        string sql,
        IReadOnlyList<QueryParameter> parameters,
        Func<DbDataReader, CancellationToken, Task<T>> read,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var command = _connection.CreateCommand();
            command.BindByName = true;
            command.InitialLONGFetchSize = LongFetchSize;
            command.CommandText = sql;
            foreach (var parameter in parameters)
            {
                command.Parameters.Add(ToOracleParameter(parameter));
            }

            await using var cancelRegistration = cancellationToken.Register(static c => ((OracleCommand)c!).Cancel(), command);
            try
            {
                await using var reader = (OracleDataReader)await command.ExecuteReaderAsync(cancellationToken);
                reader.SuppressGetDecimalInvalidCastException = true; // NUMBER(38) exceeds decimal
                return await read(reader, cancellationToken);
            }
            catch (OracleException ex) when (ex.Number == UserCancelledErrorNumber)
            {
                throw new OperationCanceledException("The query was cancelled.", ex, cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();
        _gate.Dispose();
    }

    private static OracleParameter ToOracleParameter(QueryParameter parameter)
    {
        var result = new OracleParameter(parameter.Name, parameter.Value ?? DBNull.Value);
        switch (parameter.Type)
        {
            case OracleTypeHint.Varchar2:
                result.OracleDbType = OracleDbType.Varchar2;
                break;
            case OracleTypeHint.Char:
                result.OracleDbType = OracleDbType.Char;
                break;
            case OracleTypeHint.Number:
                result.OracleDbType = OracleDbType.Decimal;
                break;
            case OracleTypeHint.Date:
                result.OracleDbType = OracleDbType.Date;
                break;
            case OracleTypeHint.TimeStamp:
                result.OracleDbType = OracleDbType.TimeStamp;
                break;
        }

        return result;
    }
}
