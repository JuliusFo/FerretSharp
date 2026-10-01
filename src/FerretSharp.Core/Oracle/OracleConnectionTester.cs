using System.Diagnostics;
using System.Text.RegularExpressions;
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
            return ConnectionTestResult.Failed(stopwatch.Elapsed, CleanMessage(ex.Message), $"ORA-{ex.Number:00000}");
        }
        catch (Exception ex) when (ex is ConnectionConfigurationException or InvalidOperationException or ArgumentException)
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
}
