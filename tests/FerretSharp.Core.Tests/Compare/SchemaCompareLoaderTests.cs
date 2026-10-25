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

    private ConnectionManager _connections = null!;

    private async Task<SchemaCompareLoader> LoaderAsync()
    {
        var store = Substitute.For<IConnectionStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns((IReadOnlyList<ConnectionProfile>)[_dev, _test]);
        _connections = new ConnectionManager(store, _secrets);
        await _connections.LoadAsync(Ct);
        return new SchemaCompareLoader(_connections, _connector);
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
            [new CompareSide(_dev.Id), new CompareSide(_test.Id), new CompareSide(_dev.Id, "erp_test")], null, Ct);

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

        var results = await loader.LoadAsync([new CompareSide(_dev.Id), new CompareSide(_test.Id)], null, Ct);

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

        var first = await loader.LoadAsync([new CompareSide(_dev.Id), new CompareSide(_test.Id)], null, Ct);
        _connections.SetPassword(_test.Id, "getippt", remember: false); // as the page does
        var again = await loader.LoadAsync([new CompareSide(_test.Id)], null, Ct);

        Assert.True(first[1].NeedsPassword);
        Assert.True(again[0].Loaded);
        await _connector.Received(1).OpenAsync(_test, "getippt", Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>Review 3.31.0, A12: a mistyped password stayed until restart, and the page no longer asked for one.</summary>
    [Fact]
    public async Task A_wrong_typed_password_is_forgotten_and_asked_for_again()
    {
        _secrets.GetPassword(_test.Id).Returns((string?)null);
        _connector.OpenAsync(_test, "falsch", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new DatabaseException("ungültiger Benutzername/Passwort", ConnectionManager.WrongPasswordCode));
        var loader = await LoaderAsync();
        _connections.SetPassword(_test.Id, "falsch", remember: false);

        var result = Assert.Single(await loader.LoadAsync([new CompareSide(_test.Id)], null, Ct));

        Assert.True(result.NeedsPassword);
        Assert.False(_connections.HasPassword(_test.Id));
    }

    [Fact]
    public async Task A_deleted_connection_is_reported()
    {
        var loader = await LoaderAsync();

        var result = Assert.Single(await loader.LoadAsync([new CompareSide(Guid.NewGuid())], null, Ct));

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

        var results = await loader.LoadAsync([new CompareSide(_dev.Id), new CompareSide(_dev.Id, "OTHER")], null, Ct);

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

        await loader.LoadAsync([new CompareSide(_dev.Id)], (side, step) => { lock (steps) { steps.Add((side, step)); } }, Ct);

        Assert.Equal([(0, "Verbinde"), (0, "Spalten")], steps);
    }

    /// <summary>
    /// Started from the UI thread, the sessions open and read on the thread pool: <c>Task.Yield</c> came back to the UI
    /// thread's context, and the whole snapshot was read there – the window stopped responding while comparing.
    /// </summary>
    [Fact]
    public async Task Sides_are_read_off_the_callers_thread()
    {
        using var ui = new UiThread();
        _secrets.GetPassword(Arg.Any<Guid>()).Returns("geheim");
        var onUi = new List<string>();
        foreach (var profile in new[] { _dev, _test })
        {
            var reader = Substitute.For<ISchemaReader>();
            reader.ReadSnapshotAsync(Arg.Any<string>(), Arg.Any<IProgress<string>?>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                Note("read " + profile.Name);
                return Snapshot(call.Arg<string>());
            });
            var connection = Substitute.For<IDatabaseConnection>();
            connection.Schema.Returns(reader);
            _connector.OpenAsync(profile, Arg.Any<string>(), SchemaCompareLoader.SessionAction, Arg.Any<CancellationToken>()).Returns(_ =>
            {
                Note("open " + profile.Name);
                return connection;
            });
        }

        var loader = await LoaderAsync();

        var results = await ui.RunAsync(() => loader.LoadAsync([new CompareSide(_dev.Id), new CompareSide(_test.Id)], null, Ct));

        Assert.All(results, r => Assert.True(r.Loaded));
        Assert.Empty(onUi);

        void Note(string what)
        {
            if (ui.IsCurrent)
            {
                lock (onUi)
                {
                    onUi.Add(what);
                }
            }
        }
    }

    [Fact]
    public async Task Saved_comparisons_survive_a_round_trip()
    {
        using var folder = new TestFolder();
        var store = new ComparisonStore(folder.Combine("data", "comparisons.json")); // the folder does not exist yet
        Assert.Empty(await store.LoadAsync(Ct));
        var saved = new SavedComparison(Guid.NewGuid(), "ERP: Dev/Test/Prod", [new CompareSide(_dev.Id), new CompareSide(_test.Id, "ERP")],
            Reference: 0, ColumnOrder: true, OnlyDifferences: false, Kinds: [CompareKind.Column, CompareKind.Index]);

        await store.SaveAsync([saved], Ct);
        var loaded = Assert.Single(await store.LoadAsync(Ct));

        Assert.Equal(saved with { Sides = loaded.Sides, Kinds = loaded.Kinds }, loaded);
        Assert.Equal(saved.Sides, loaded.Sides);
        Assert.Equal(saved.Kinds, loaded.Kinds);
    }

    /// <summary>R5b (review 3.31.0, B2): an entry set to null by hand is left out; a broken file says so, a newer one with an unknown shape is newer.</summary>
    [Theory]
    [InlineData("""{ "version": 1, "comparisons": [ null ] }""", null)]
    [InlineData("""{ "version": 1, "comparisons": null }""", null)]
    [InlineData("""{ "version": 1, "comparisons": [ { "id": 1 } ] }""", "keine gültige Datei mit Schema-Vergleichen")]
    [InlineData("""{ "version": 3, "comparisons": 42 }""", "neueren FerretSharp-Version")]
    public async Task Files_edited_by_hand(string content, string? error)
    {
        using var folder = new TestFolder();
        var file = folder.Combine("comparisons.json");
        await File.WriteAllTextAsync(file, content, Ct);
        var store = new ComparisonStore(file);

        if (error is null)
        {
            Assert.Empty(await store.LoadAsync(Ct));
        }
        else
        {
            Assert.Contains(error, (await Assert.ThrowsAsync<IOException>(() => store.LoadAsync(Ct))).Message);
        }
    }

    /// <summary>A file of a newer FerretSharp (after a downgrade) is reported and kept, not overwritten as format 1.</summary>
    [Fact]
    public async Task Comparisons_of_a_newer_format_are_neither_read_nor_overwritten()
    {
        using var folder = new TestFolder();
        var file = folder.Combine("comparisons.json");
        const string newer = """{ "version": 2, "comparisons": [], "somethingNew": true }""";
        await File.WriteAllTextAsync(file, newer, Ct);
        var store = new ComparisonStore(file);

        var load = await Assert.ThrowsAsync<IOException>(() => store.LoadAsync(Ct));
        var save = await Assert.ThrowsAsync<IOException>(() => store.SaveAsync([], Ct));

        Assert.Contains("neueren FerretSharp-Version", load.Message);
        Assert.Contains("nicht überschrieben", save.Message);
        Assert.Equal(newer, await File.ReadAllTextAsync(file, Ct));
    }
}
