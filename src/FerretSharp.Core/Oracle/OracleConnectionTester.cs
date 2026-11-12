using System.Diagnostics;
using System.Text.RegularExpressions;
using FerretSharp.Core.Connections;
using Oracle.ManagedDataAccess.Client;

namespace FerretSharp.Core.Oracle;

/// <summary>
/// "Test connection": opens a session like the explorer does, through <see cref="SessionOpening"/> – a single attempt hung
/// for about 20 s when opening hangs over the VPN (fix/robustness). A refused login ends the test as soon as it is answered;
/// the same trade-off as connecting: Oracle answers logins late after failed ones, and an answer later than the first limit
/// (5 s) finds the second attempt started – one more login that counts towards <c>FAILED_LOGIN_ATTEMPTS</c> (code review).
/// </summary>
public sealed class OracleConnectionTester(IDatabaseConnector connector, TimeProvider time) : IConnectionTester
{
    public OracleConnectionTester()
        : this(new OracleDatabaseConnector(), TimeProvider.System)
    {
    }

    public async Task<ConnectionTestResult> TestAsync(ConnectionProfile profile, string password, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await using var connection = await SessionOpening.OpenAsync(
                (_, token) => connector.OpenAsync(profile, password, "Connection test", token), time, retrying: null, cancellationToken);
            await connection.PingIfIdleAsync(TimeSpan.Zero, cancellationToken);
            return ConnectionTestResult.Ok(stopwatch.Elapsed, connection.ServerVersion);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DatabaseException ex)
        {
            return ConnectionTestResult.Failed(stopwatch.Elapsed, CleanMessage(ex.Message), ex.ErrorCode);
        }
        catch (Exception ex) when (ex is ConnectionConfigurationException or OracleException or InvalidOperationException or ArgumentException)
        {
            return ConnectionTestResult.Failed(stopwatch.Elapsed, CleanMessage(ex.Message));
        }
    }

    /// <summary>First line only, without the leading "ORA-01017: " (the code is reported separately).</summary>
    internal static string CleanMessage(string message)
    {
        var newline = message.IndexOfAny(['\r', '\n']);
        var firstLine = newline < 0 ? message : message[..newline];
        return Regex.Replace(firstLine, @"^ORA-\d{5}:\s*", "");
    }
}

public static class OracleSessionDefaults
{
    public const string Module = "FerretSharp";

    /// <summary>
    /// A tag for <c>CLIENT_INFO</c> that tells one session from all others (WP-52, ADR 0027): the explorer session finds it in
    /// <c>V$SESSION</c> while it runs a statement, and a blocking session with such a tag is one of this instance's workspaces.
    /// ASCII only (see <see cref="OracleSession.ToSessionAttribute"/>), well under 64 characters.
    /// </summary>
    public static string NewSessionTag() => $"{Module} {Guid.NewGuid():N}"[..(Module.Length + 13)];
}
