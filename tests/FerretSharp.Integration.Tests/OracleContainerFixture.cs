using DotNet.Testcontainers.Builders;
using Testcontainers.Oracle;

[assembly: AssemblyFixture(typeof(FerretSharp.Integration.Tests.OracleContainerFixture))]

namespace FerretSharp.Integration.Tests;

/// <summary>
/// Starts one Oracle Free container per test run. If Docker is not available,
/// tests that call <see cref="RequireConnectionString"/> are skipped instead of failing.
/// </summary>
public sealed class OracleContainerFixture : IAsyncLifetime
{
    public const string Image = "gvenzl/oracle-free:23-slim-faststart";

    private OracleContainer? _container;

    public string? ConnectionString { get; private set; }

    public string? UnavailableReason { get; private set; }

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
    }

    public string RequireConnectionString()
    {
        Assert.SkipWhen(ConnectionString is null, UnavailableReason ?? "Oracle container was not started.");
        return ConnectionString!;
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}
