using FerretSharp.Core.Compare;
using FerretSharp.Core.Connections;
using FerretSharp.Core.Schema;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace FerretSharp.Core.Tests.Compare;

/// <summary>Loading the sides of a schema comparison: one session per connection, failures per side.</summary>
public sealed class SchemaCompareLoaderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly ConnectionProfile _dev = Profile("ERP Dev", "erp");
    private readonly ConnectionProfile _test = Profile("ERP Test", null);
    private readonly ISecretStore _secrets = Substitute.For<ISecretStore>();
    private readonly IDatabaseConnector _connector = Substitute.For<IDatabaseConnector>();

    private static ConnectionProfile Profile(string name, string? schema) =>
        new(Guid.NewGuid(), name, ConnectionKind.Dev, new HostPortAddress("db", 1521, "FREEPDB1", null), "APP", schema, false);

    private static SchemaSnapshot Snapshot(string owner) => new(owner, DateTimeOffset.Now, []);

    private async Task<SchemaCompareLoader> LoaderAsync()
    {
        var store = Substitute.For<IConnectionStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns((IReadOnlyList<ConnectionProfile>)[_dev, _test]);
        var connections = new ConnectionManager(store, _secrets);
        await connections.LoadAsync(Ct);
        return new SchemaCompareLoader(connections, _connector);
    }

    private IDatabaseConnection Connection(ConnectionProfile profile)
    {
        var reader = Substitute.For<ISchemaReader>();
        reader.ReadSnapshotAsync(Arg.Any<string>(), Arg.Any<IProgress<string>?>(), Arg.Any<CancellationToken>())
            .Returns(call => Snapshot(call.Arg<string>()));
        var connection = Substitute.For<IDatabaseConnection>();
        connection.Schema.Returns(reader);
        _connector.OpenAsync(profile, Arg.Any<string>(), SchemaCompareLoader.SessionAction, Arg.Any<CancellationToken>()).Returns(connection);
        return connection;
    }

    [Fact]
    public async Task Sides_of_one_connection_share_a_session_which_is_closed_afterwards()
    {
        _secrets.GetPassword(Arg.Any<Guid>()).Returns("geheim");
        var dev = Connection(_dev);
        var test = Connection(_test);
        var loader = await LoaderAsync();

        var results = await loader.LoadAsync(
            [new CompareSide(_dev.Id), new CompareSide(_test.Id), new CompareSide(_dev.Id, "erp_test")], null, null, Ct);

        Assert.Equal(["ERP", "APP", "ERP_TEST"], results.Select(r => r.Snapshot!.Owner)); // profile schema, user, typed schema
        await _connector.Received(1).OpenAsync(_dev, "geheim", SchemaCompareLoader.SessionAction, Arg.Any<CancellationToken>());
        await dev.Received(1).DisposeAsync();
        await test.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task A_failing_side_does_not_stop_the_others()
    {
        _secrets.GetPassword(Arg.Any<Guid>()).Returns("geheim");
        Connection(_dev);
        _connector.OpenAsync(_test, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new DatabaseException("TNS: keine Verbindung", "ORA-12541"));
        var loader = await LoaderAsync();

        var results = await loader.LoadAsync([new CompareSide(_dev.Id), new CompareSide(_test.Id)], null, null, Ct);

        Assert.True(results[0].Loaded);
        Assert.Equal("ORA-12541", results[1].Error!.ErrorCode);
        Assert.False(results[1].NeedsPassword);
    }

    [Fact]
    public async Task Without_a_password_the_side_asks_for_one_and_a_typed_one_is_used()
    {
        _secrets.GetPassword(_dev.Id).Returns("geheim");
        _secrets.GetPassword(_test.Id).Returns((string?)null); // NSubstitute would answer ""
        Connection(_dev);
        Connection(_test);
        var loader = await LoaderAsync();

        var first = await loader.LoadAsync([new CompareSide(_dev.Id), new CompareSide(_test.Id)], null, null, Ct);
        var again = await loader.LoadAsync([new CompareSide(_test.Id)], new Dictionary<Guid, string> { [_test.Id] = "getippt" }, null, Ct);

        Assert.True(first[1].NeedsPassword);
        Assert.True(again[0].Loaded);
        await _connector.Received(1).OpenAsync(_test, "getippt", Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_deleted_connection_is_reported()
    {
        var loader = await LoaderAsync();

        var result = Assert.Single(await loader.LoadAsync([new CompareSide(Guid.NewGuid())], null, null, Ct));

        Assert.Equal("Diese Verbindung gibt es nicht mehr.", result.Error!.Message);
    }

    [Fact]
    public async Task A_lost_connection_fails_the_remaining_sides_of_that_connection()
    {
        _secrets.GetPassword(Arg.Any<Guid>()).Returns("geheim");
        var dev = Connection(_dev);
        dev.Schema.ReadSnapshotAsync("ERP", Arg.Any<IProgress<string>?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new DatabaseException("Netzwerksession: Dateiende", "ORA-12537"));
        var loader = await LoaderAsync();

        var results = await loader.LoadAsync([new CompareSide(_dev.Id), new CompareSide(_dev.Id, "OTHER")], null, null, Ct);

        Assert.All(results, r => Assert.Equal("ORA-12537", r.Error!.ErrorCode));
        await dev.Schema.DidNotReceive().ReadSnapshotAsync("OTHER", Arg.Any<IProgress<string>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Progress_names_the_side()
    {
        _secrets.GetPassword(Arg.Any<Guid>()).Returns("geheim");
        var dev = Connection(_dev);
        dev.Schema.ReadSnapshotAsync(Arg.Any<string>(), Arg.Any<IProgress<string>?>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            call.Arg<IProgress<string>?>()!.Report("Spalten");
            return Snapshot("ERP");
        });
        var loader = await LoaderAsync();
        var steps = new List<(int, string)>();

        await loader.LoadAsync([new CompareSide(_dev.Id)], null, (side, step) => { lock (steps) { steps.Add((side, step)); } }, Ct);

        Assert.Equal([(0, "Verbinde"), (0, "Spalten")], steps);
    }

    [Fact]
    public async Task Saved_comparisons_survive_a_round_trip()
    {
        var file = Path.Combine(Path.GetTempPath(), "ferretsharp-tests", Guid.NewGuid().ToString("N"), "comparisons.json");
        try
        {
            var store = new ComparisonStore(file);
            Assert.Empty(await store.LoadAsync(Ct));
            var saved = new SavedComparison(Guid.NewGuid(), "ERP: Dev/Test/Prod", [new CompareSide(_dev.Id), new CompareSide(_test.Id, "ERP")],
                Reference: 0, ColumnOrder: true, OnlyDifferences: false, Kinds: [CompareKind.Column, CompareKind.Index]);

            await store.SaveAsync([saved], Ct);
            var loaded = Assert.Single(await store.LoadAsync(Ct));

            Assert.Equal(saved with { Sides = loaded.Sides, Kinds = loaded.Kinds }, loaded);
            Assert.Equal(saved.Sides, loaded.Sides);
            Assert.Equal(saved.Kinds, loaded.Kinds);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(file)!, recursive: true);
        }
    }
}
