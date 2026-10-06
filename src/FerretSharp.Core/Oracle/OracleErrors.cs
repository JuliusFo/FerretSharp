using FerretSharp.Core.Connections;
using FerretSharp.Core.Query;
using Oracle.ManagedDataAccess.Client;

namespace FerretSharp.Core.Oracle;

/// <summary>
/// A statement failed; carries it for the error dialog. <see cref="Exception.InnerException"/> is the
/// <see cref="OracleException"/>, or null if the connection was already closed. Never leaves <see cref="OracleSession"/>:
/// its public methods translate it (<see cref="OracleErrors.Translate"/>).
/// </summary>
internal sealed class OracleStatementException(string sql, IReadOnlyList<QueryParameter> parameters, OracleException? inner)
    : Exception(inner?.Message ?? "Die Verbindung zur Datenbank ist getrennt.", inner)
{
    public QuerySpec Statement { get; } = new(sql, parameters);

    public OracleException? Oracle { get; } = inner;
}

/// <summary>The Oracle errors FerretSharp reacts to – one place instead of numbers spread over the code.</summary>
internal static class OracleErrorCodes
{
    /// <summary>ORA-01013: user requested cancel of current operation.</summary>
    public const int UserCancelled = 1013;

    /// <summary>ORA-02091: transaction rolled back (a deferred constraint failed at commit).</summary>
    public const int TransactionRolledBack = 2091;

    /// <summary>ORA-01466: table definition changed since the snapshot (also within about a second after DDL).</summary>
    public const int DefinitionChanged = 1466;

    /// <summary>
    /// A read-only snapshot that cannot serve the query, fixed by a new one: snapshot too old (ORA-01555), table definition
    /// changed (ORA-01466), segment created since the snapshot (ORA-08176, first row of a table with deferred segments).
    /// </summary>
    public static readonly IReadOnlySet<int> SnapshotUnusable = new HashSet<int> { 1555, DefinitionChanged, 8176 };

    /// <summary>Row locked by another session: WAIT timeout (ORA-30006; Oracle 23 reports ORA-00054 instead) or NOWAIT.</summary>
    public static readonly IReadOnlySet<int> RowLocked = new HashSet<int> { 30006, 54 };

    /// <summary>No rights on a dictionary view (V$…): table or view does not exist (ORA-00942), insufficient privileges (ORA-01031).</summary>
    public static readonly IReadOnlySet<int> MissingRights = new HashSet<int> { 942, 1031 };

    /// <summary>"ORA-00942".</summary>
    public static string Code(int number) => $"ORA-{number:00000}";

    /// <summary>The error is one of <paramref name="numbers"/>.</summary>
    public static bool IsAny(this DatabaseException error, IReadOnlySet<int> numbers) =>
        error.ErrorCode is { } code && numbers.Any(n => Code(n) == code);
}

internal static class OracleErrors
{
    /// <summary>Maps driver/configuration failures to <see cref="DatabaseException"/>; null for anything else.</summary>
    public static DatabaseException? Translate(Exception ex) => ex switch
    {
        OracleStatementException { Oracle: { } oracle } statement => new DatabaseException(
            OracleConnectionTester.CleanMessage(oracle.Message), OracleErrorCodes.Code(oracle.Number), oracle) { Statement = statement.Statement },
        OracleStatementException closed => new DatabaseException(closed.Message, inner: closed)
        {
            Statement = closed.Statement,
            IsConnectionLost = true,
        },
        OracleException oracle => new DatabaseException(
            OracleConnectionTester.CleanMessage(oracle.Message), OracleErrorCodes.Code(oracle.Number), oracle),
        ConnectionConfigurationException config => new DatabaseException(config.Message, inner: config),
        _ => null,
    };
}
