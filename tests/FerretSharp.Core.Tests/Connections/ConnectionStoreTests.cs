using FerretSharp.Core.Connections;

namespace FerretSharp.Core.Tests.Connections;

public sealed class ConnectionStoreTests : IDisposable
{
    private readonly TestFolder _folder = new();

    private string FilePath => _folder.Combine("connections.json");

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
        Assert.DoesNotContain("display", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("effectiveSchema", json, StringComparison.OrdinalIgnoreCase);
        Assert.Single(Directory.GetFiles(_folder.Path));
    }

    [Fact]
    public async Task Corrupt_file_raises_store_exception()
    {
        var ct = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(FilePath, "{ not json", ct);

        await Assert.ThrowsAsync<ConnectionStoreException>(() => new ConnectionStore(FilePath).LoadAsync(ct));
    }

    [Fact]
    public async Task Newer_format_version_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(FilePath, """{ "version": 99, "connections": [] }""", ct);

        var ex = await Assert.ThrowsAsync<ConnectionStoreException>(() => new ConnectionStore(FilePath).LoadAsync(ct));
        Assert.Contains("neueren", ex.Message);
    }

    /// <summary>
    /// Review 3.31.0, A6: these got through as other exceptions – the manager then saw no load error, and the first new
    /// connection replaced all others in the file.
    /// </summary>
    [Theory]
    [InlineData("""{ "version": 1, "connections": [ { "id": "6f1e1b6e-0000-0000-0000-000000000001", "name": "A", "kind": "Dev", "address": { "host": "db", "port": 1521, "serviceName": "S" }, "user": "u", "readOnly": false } ] }""")]
    [InlineData("""{ "version": 1, "connections": [ null ] }""")]
    [InlineData("""{ "version": 1, "connections": [ { "name": "ohne Adresse", "kind": "Dev", "user": "u" } ] }""")]
    [InlineData("""{ "version": 99, "connections": [ { "address": { "type": "neu" } } ] }""")]
    public async Task Every_unreadable_file_raises_a_store_exception(string content)
    {
        var ct = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(FilePath, content, ct);

        await Assert.ThrowsAsync<ConnectionStoreException>(() => new ConnectionStore(FilePath).LoadAsync(ct));
    }

    [Fact]
    public async Task A_file_without_a_list_has_no_connections()
    {
        var ct = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(FilePath, """{ "version": 1 }""", ct);

        Assert.Empty(await new ConnectionStore(FilePath).LoadAsync(ct));
    }

    [Fact]
    public async Task A_locked_file_raises_a_store_exception()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows locks a file against reading.");
        var ct = TestContext.Current.CancellationToken;
        await new ConnectionStore(FilePath).SaveAsync([TestProfiles.HostPort()], ct);
        await using var locked = new FileStream(FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        await Assert.ThrowsAsync<ConnectionStoreException>(() => new ConnectionStore(FilePath).LoadAsync(ct));
    }

    public void Dispose()
    {
        _folder.Dispose();
    }
}
