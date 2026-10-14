using System.Globalization;
using System.Text.RegularExpressions;
using DotNet.Testcontainers.Builders;
using FerretSharp.Core.Connections;
using Oracle.ManagedDataAccess.Client;
using Testcontainers.Oracle;

[assembly: AssemblyFixture(typeof(FerretSharp.Integration.Tests.OracleContainerFixture))]

namespace FerretSharp.Integration.Tests;

/// <summary>
/// Starts one Oracle Free container per test run. If Docker is not available or the container does not start, tests that
/// call <see cref="RequireConnectionString"/> are skipped with the reason instead of failing – and the tests that need no
/// database still run. In the CI only a missing Docker skips; any other start failure fails the run.
/// </summary>
public sealed class OracleContainerFixture : IAsyncLifetime
{
    public const string Image = "gvenzl/oracle-free:23-slim-faststart";

    private OracleContainer? _container;

    public string? ConnectionString { get; private set; }

    public string? UnavailableReason { get; private set; }

    /// <summary>GitHub Actions (and most CI systems) set CI=true.</summary>
    internal static bool InCi => string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase);

    public async ValueTask InitializeAsync()
    {
        try
        {
            _container = new OracleBuilder(Image).Build();
            await _container.StartAsync();
            ConnectionString = _container.GetConnectionString();
        }
        catch (DockerUnavailableException ex)
        {
            UnavailableReason = $"Docker is not available: {ex.Message}";
        }
        catch (Exception ex) when (!InCi)
        {
            UnavailableReason = $"The Oracle container did not start: {ex.GetType().Name}: {ex.Message}";
        }
    }

    public string RequireConnectionString()
    {
        Assert.SkipWhen(ConnectionString is null, UnavailableReason ?? "Oracle container was not started.");
        return ConnectionString!;
    }

    /// <summary>A FerretSharp connection profile (host/port + service) pointing at the container, plus its password.</summary>
    public (ConnectionProfile Profile, string Password) RequireProfile()
    {
        var builder = new OracleConnectionStringBuilder(RequireConnectionString());
        // Testcontainers hands out a connect descriptor: (DESCRIPTION=(ADDRESS=(...)(HOST=h)(PORT=p))(CONNECT_DATA=(SERVICE_NAME=s)))
        var match = Regex.Match(
            builder.DataSource,
            @"\(HOST=(?<host>[^)]+)\).*\(PORT=(?<port>\d+)\).*\(SERVICE_NAME=(?<service>[^)]+)\)",
            RegexOptions.IgnoreCase);
        Assert.True(match.Success, $"Unexpected data source format: {builder.DataSource}");

        var address = new HostPortAddress(
            match.Groups["host"].Value,
            int.Parse(match.Groups["port"].Value, CultureInfo.InvariantCulture),
            match.Groups["service"].Value,
            Sid: null);
        var profile = new ConnectionProfile(Guid.NewGuid(), "Container", ConnectionKind.Test, address, builder.UserID, null, false);
        return (profile, builder.Password);
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}
