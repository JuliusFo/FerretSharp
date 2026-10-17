using FerretSharp.Core.Compare;
using FerretSharp.Core.Connections;
using FerretSharp.Core.Schema;
using FerretSharp.Core.Tests.Fakes;
using FerretSharp.UI.Resources;
using FerretSharp.UI.State;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace FerretSharp.UI.Tests.State;

/// <summary>
/// The schema comparison page's state (WP-20) over the real loader and store: which sides load, a password asked for
/// only for the side that needs it, options that recompute and sides that start over, cancelling, the saved comparisons.
/// </summary>
public sealed class SchemaCompareServiceTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TestFolder _folder = new();
    private readonly InMemorySecretStore _secrets = new();
    private readonly IDatabaseConnector _connector = Substitute.For<IDatabaseConnector>();
    private readonly ConnectionProfile _dev = TestApp.Profile("ERP Dev", ConnectionKind.Dev);
    private readonly ConnectionProfile _test = TestApp.Profile("ERP Test");
    private ConnectionManager _connections = null!;

    private string StoreFile => _folder.Combine("comparisons.json");

    private async Task<SchemaCompareService> ServiceAsync()
    {
        var store = Substitute.For<IConnectionStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns((IReadOnlyList<ConnectionProfile>)[_dev, _test]);
        _connections = new ConnectionManager(store, _secrets);
        await _connections.LoadAsync(Ct);
        var service = new SchemaCompareService(
            new ComparisonStore(StoreFile), new SchemaCompareLoader(_connections, _connector), _connections, NullLogger<SchemaCompareService>.Instance);
        await service.EnsureLoadedAsync();
        return service;
    }

    /// <summary>The profile's session reads a snapshot with one table named after the profile.</summary>
    private void Opens(ConnectionProfile profile)
    {
        var reader = Substitute.For<ISchemaReader>();
        reader.ReadSnapshotAsync(Arg.Any<string>(), Arg.Any<IProgress<string>?>(), Arg.Any<CancellationToken>())
            .Returns(call => new SchemaSnapshot(call.Arg<string>(), DateTimeOffset.Now, []));
        var connection = FakeDatabase.Connection(reader); // before: NSubstitute cannot configure a substitute inside Returns()
        _connector.OpenAsync(profile, Arg.Any<string>(), SchemaCompareLoader.SessionAction, Arg.Any<CancellationToken>())
            .Returns(connection);
    }

    private static void SetSides(SchemaCompareService service, params Guid[] connections) =>
        service.SetSides(connections.Select(id => new CompareSide(id)).ToList(), reference: null);

    [Fact]
    public async Task Comparing_builds_the_matrix_over_the_sides_that_loaded()
    {
        _secrets.SetPassword(_dev.Id, "pw");
        _secrets.SetPassword(_test.Id, "pw");
        Opens(_dev);
        _connector.OpenAsync(_test, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new DatabaseException("TNS: keine Verbindung", "ORA-12541"));
        var service = await ServiceAsync();
        SetSides(service, _dev.Id, _test.Id);

        await service.CompareAsync();

        Assert.False(service.Loading);
        Assert.All(service.Sides, side => Assert.Equal((false, null), (side.Loading, side.Step)));
        Assert.NotNull(service.Sides[0].Snapshot);
        Assert.Equal(("ORA-12541", false), (service.Sides[1].Error?.ErrorCode, service.Sides[1].NeedsPassword));
        Assert.Equal([0], service.ResultSides); // the failed side is left out of the matrix
        Assert.NotNull(service.Result);
    }

    [Fact]
    public async Task A_side_without_a_password_asks_for_one_and_only_that_side_loads_again()
    {
        _secrets.SetPassword(_dev.Id, "pw");
        Opens(_dev);
        Opens(_test);
        var service = await ServiceAsync();
        SetSides(service, _dev.Id, _test.Id);

        await service.CompareAsync();
        Assert.True(service.Sides[1].NeedsPassword);
        Assert.Equal([0], service.ResultSides);

        service.SetPassword(_test.Id, "typed");
        await service.CompareAsync(onlyMissing: true);

        Assert.Equal([0, 1], service.ResultSides);
        await _connector.Received(1).OpenAsync(_dev, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _connector.Received(1).OpenAsync(_test, "typed", Arg.Any<string>(), Arg.Any<CancellationToken>());
        Assert.Null(_connections.GetStoredPassword(_test.Id)); // typed for this run, not stored (A12)
    }

    [Fact]
    public async Task Options_recompute_the_matrix_while_other_sides_start_over()
    {
        _secrets.SetPassword(_dev.Id, "pw");
        _secrets.SetPassword(_test.Id, "pw");
        Opens(_dev);
        Opens(_test);
        var service = await ServiceAsync();
        SetSides(service, _dev.Id, _test.Id);
        await service.CompareAsync();
        var snapshot = service.Sides[1].Snapshot;

        service.SetOptions(reference: 1, columnOrder: true, onlyDifferences: false, kinds: [CompareKind.Column]);

        Assert.Same(snapshot, service.Sides[1].Snapshot); // nothing read again
        Assert.NotNull(service.Result);
        Assert.Equal((1, true, false), (service.Current.Reference, service.Current.ColumnOrder, service.Current.OnlyDifferences));

        service.SetSides([new CompareSide(_dev.Id), new CompareSide(_test.Id, "erp")], reference: 2);

        Assert.All(service.Sides, side => Assert.Null(side.Snapshot));
        Assert.Equal((null, []), (service.Result, service.ResultSides));
        Assert.Null(service.Current.Reference); // the reference pointed past the sides
        await _connector.Received(1).OpenAsync(_test, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Cancelling_leaves_the_sides_empty()
    {
        _secrets.SetPassword(_dev.Id, "pw");
        var opening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _connector.OpenAsync(_dev, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                opening.TrySetResult();
                await Task.Delay(Timeout.Infinite, call.ArgAt<CancellationToken>(3));
                return FakeDatabase.Connection();
            });
        var service = await ServiceAsync();
        SetSides(service, _dev.Id);

        var comparing = service.CompareAsync();
        await opening.Task.WaitAsync(Ct);
        Assert.True(service.Loading);
        service.Cancel();
        await comparing.WaitAsync(Ct);

        Assert.False(service.Loading);
        Assert.Equal((false, null, null), (service.Sides[0].Loading, service.Sides[0].Snapshot, service.Result));
    }

    [Fact]
    public async Task Sides_without_a_connection_are_not_compared()
    {
        var service = await ServiceAsync(); // a new comparison: two sides without a connection

        await service.CompareAsync();

        Assert.Equal(2, service.Sides.Count);
        Assert.Null(service.Result);
        await _connector.DidNotReceiveWithAnyArgs().OpenAsync(default!, default!, default!, Ct);
    }

    [Fact]
    public async Task Saved_comparisons_are_kept_sorted_and_deleting_the_open_one_keeps_it_on_the_page()
    {
        var service = await ServiceAsync();
        Assert.Empty(service.Saved); // no file yet
        SetSides(service, _test.Id, _dev.Id);

        await service.SaveAsync("  Test/Dev ", asNew: false);
        var first = service.Current;
        service.New();
        SetSides(service, _dev.Id);
        await service.SaveAsync("Dev", asNew: false);
        await service.SaveAsync("Dev (Kopie)", asNew: true);

        Assert.Equal(["Dev", "Dev (Kopie)", "Test/Dev"], service.Saved.Select(s => s.Name));
        Assert.True(service.IsSaved);

        service.Open(first);
        Assert.Equal([_test.Id, _dev.Id], service.Sides.Select(s => s.Side.ConnectionId));
        await service.DeleteAsync(first.Id);

        Assert.False(service.IsSaved);
        Assert.Equal(first.Sides, service.Current.Sides); // stays on the page, now unsaved
        var again = await ServiceAsync();
        Assert.Equal(["Dev", "Dev (Kopie)"], again.Saved.Select(s => s.Name));
        Assert.Null(again.StoreError);
    }

    /// <summary>
    /// Review 3.40.0, ST-8 (decision of the user: like the filter presets): after a read that failed, the next save replaced
    /// every saved comparison with the new one.
    /// </summary>
    [Fact]
    public async Task A_broken_file_is_not_overwritten_by_the_next_save()
    {
        const string broken = """{ "version": 1, "comparisons": [ { "id": """;
        await File.WriteAllTextAsync(StoreFile, broken, Ct);
        var service = await ServiceAsync();
        Assert.NotNull(service.StoreError);

        await service.SaveAsync("Dev", asNew: false);

        Assert.StartsWith("Nicht gespeichert: Die gespeicherten Vergleiche ließen sich nicht lesen", service.StoreError);
        Assert.Equal(broken, await File.ReadAllTextAsync(StoreFile, Ct));
    }

    [Fact]
    public async Task A_file_that_was_locked_is_read_again_before_saving_and_keeps_its_comparisons()
    {
        var earlier = await ServiceAsync();
        SetSides(earlier, _test.Id);
        await earlier.SaveAsync("Vorhanden", asNew: false);
        SchemaCompareService service;
        await using (File.Open(StoreFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) // another program holds it
        {
            service = await ServiceAsync();
            Assert.NotNull(service.StoreError);
            Assert.Empty(service.Saved);
        }

        SetSides(service, _dev.Id);
        await service.SaveAsync("Neu", asNew: false);

        Assert.Null(service.StoreError);
        Assert.Equal(["Neu", "Vorhanden"], service.Saved.Select(s => s.Name));
    }

    [Fact]
    public async Task A_file_of_a_newer_version_is_reported_and_not_overwritten()
    {
        const string newer = """{ "version": 2, "comparisons": [] }""";
        await File.WriteAllTextAsync(StoreFile, newer, Ct);
        var service = await ServiceAsync();

        Assert.NotNull(service.StoreError);
        Assert.Equal(2, service.Sides.Count); // the page works without the saved ones

        await service.SaveAsync("Dev", asNew: false);

        Assert.NotNull(service.StoreError);
        Assert.Empty(service.Saved);
        Assert.Equal(newer, await File.ReadAllTextAsync(StoreFile, Ct));
    }

    [Fact]
    public async Task A_side_is_labelled_with_its_connection_and_schema()
    {
        var service = await ServiceAsync();

        Assert.Equal("ERP Test · APP_USER", service.Label(new CompareSide(_test.Id)));
        Assert.Equal("ERP Test · ERP", service.Label(new CompareSide(_test.Id, "erp")));
        Assert.Equal(CompareViewText.Compare_MissingConnection, service.Label(new CompareSide(Guid.NewGuid())));
    }

    public void Dispose() => _folder.Dispose();
}
