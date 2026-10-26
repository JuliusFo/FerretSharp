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

    /// <summary>
    /// The image turns out-of-band breaks off on the server (<c>DISABLE_OOB=ON</c> in its sqlnet.ora, gvenzl/oci-oracle-xe#43:
    /// they failed through Docker's port proxy; Docker Desktop's loses urgent data). Sessions opened until the result is
    /// disposed negotiate them again; only for tests that run alone (<see cref="SerialTests"/>).
    /// </summary>
    public async Task<IAsyncDisposable> AllowOutOfBandBreaksAsync()
    {
        RequireConnectionString();
        await EditSqlNetAsync("s/^DISABLE_OOB=ON/#DISABLE_OOB=ON/", "^#DISABLE_OOB=ON");
        return new Restore(() => EditSqlNetAsync("s/^#DISABLE_OOB=ON/DISABLE_OOB=ON/", "^DISABLE_OOB=ON"));
    }

    /// <summary>The server reads its sqlnet.ora for every new session.</summary>
    private async Task EditSqlNetAsync(string sed, string expected)
    {
        var file = "$ORACLE_HOME/network/admin/sqlnet.ora";
        var result = await _container!.ExecAsync(["bash", "-c", $"sed -i '{sed}' {file} && grep -q '{expected}' {file}"]);
        Assert.True(result.ExitCode == 0, $"sqlnet.ora of the image changed ({sed}): {result.Stderr}");
    }

    private sealed class Restore(Func<Task> restore) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await restore();
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}
