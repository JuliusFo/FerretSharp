using FerretSharp.Core.Connections;

namespace FerretSharp.Core.Tests.Connections;

public sealed class ConnectionStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ferret-tests", Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(_directory, "connections.json");

    [Fact]
    public async Task Missing_file_yields_empty_list()
    {
        var store = new ConnectionStore(FilePath);

        var profiles = await store.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Empty(profiles);
    }

    [Fact]
    public async Task Round_trips_both_address_kinds()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new ConnectionStore(FilePath);
        ConnectionProfile[] profiles = [TestProfiles.HostPort(), TestProfiles.HostPort(service: null, sid: "ORCL"), TestProfiles.Tns()];

        await store.SaveAsync(profiles, ct);
        var loaded = await new ConnectionStore(FilePath).LoadAsync(ct);

        Assert.Equal(profiles, loaded);
    }

    [Fact]
    public async Task Writes_versioned_document_with_type_discriminator_and_no_temp_file()
    {
        var ct = TestContext.Current.CancellationToken;
        await new ConnectionStore(FilePath).SaveAsync([TestProfiles.HostPort(), TestProfiles.Tns()], ct);

        var json = await File.ReadAllTextAsync(FilePath, ct);

        Assert.Contains("\"version\": 1", json);
        Assert.Contains("\"type\": \"hostPort\"", json);
        Assert.Contains("\"type\": \"tnsAlias\"", json);
        Assert.Contains("\"kind\": \"prod\"", json);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task Corrupt_file_raises_store_exception()
    {
        var ct = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(FilePath, "{ not json", ct);

        await Assert.ThrowsAsync<ConnectionStoreException>(() => new ConnectionStore(FilePath).LoadAsync(ct));
    }

    [Fact]
    public async Task Newer_format_version_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(FilePath, """{ "version": 99, "connections": [] }""", ct);

        var ex = await Assert.ThrowsAsync<ConnectionStoreException>(() => new ConnectionStore(FilePath).LoadAsync(ct));
        Assert.Contains("neueren", ex.Message);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
