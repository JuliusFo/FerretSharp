using System.Diagnostics;
using FerretSharp.Core.Connections;
using Oracle.ManagedDataAccess.Client;

namespace FerretSharp.Core.Oracle;

public sealed class OracleConnectionTester : IConnectionTester
{
    private static readonly SessionContext TestContext = new(OracleSessionDefaults.Module, "Connection test");

    public async Task<ConnectionTestResult> TestAsync(ConnectionProfile profile, string password, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var connectionString = OracleConnectionStringFactory.Create(profile, password);
            await using var session = await OracleSession.OpenAsync(connectionString, TestContext, cancellationToken);
            await session.PingAsync(cancellationToken);
            return ConnectionTestResult.Ok(stopwatch.Elapsed, session.ServerVersion);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (OracleException ex)
        {
            return ConnectionTestResult.Failed(stopwatch.Elapsed, FirstLine(ex.Message), $"ORA-{ex.Number:00000}");
        }
        catch (Exception ex) when (ex is ConnectionConfigurationException or InvalidOperationException or ArgumentException)
        {
            return ConnectionTestResult.Failed(stopwatch.Elapsed, FirstLine(ex.Message));
        }
    }

    private static string FirstLine(string message)
    {
        var newline = message.IndexOfAny(['\r', '\n']);
        return newline < 0 ? message : message[..newline];
    }
}

public static class OracleSessionDefaults
{
    public const string Module = "FerretSharp";
}
