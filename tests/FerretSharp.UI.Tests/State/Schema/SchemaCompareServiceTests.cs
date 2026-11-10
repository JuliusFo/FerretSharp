using FerretSharp.Core.Compare;
using FerretSharp.Core.Connections;
using FerretSharp.Core.Schema;
using FerretSharp.Core.Settings;
using FerretSharp.Testing;
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
    private readonly ConnectionProfile _test = TestApp.Profile("ERP Test") with { Group = "Kunde A" };
    private ConnectionManager _connections = null!;

    private string StoreFile => _folder.Combine("comparisons.json");

    private async Task<SchemaCompareService> ServiceAsync(AppSettings? settings = null)
    {
        var store = Substitute.For<IConnectionStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns((IReadOnlyList<ConnectionProfile>)[_dev, _test]);
        _connections = new ConnectionManager(store, _secrets);
        await _connections.LoadAsync(Ct);
        var service = new SchemaCompareService(
            new ComparisonStore(StoreFile), new SchemaCompareLoader(_connections, _connector), _connections,
            new AppSettingsService(new SettingsStore(_folder.Combine("settings.json")), settings ?? AppSettings.Default),
            NullLogger<SchemaCompareService>.Instance);
        await service.EnsureLoadedAsync();
        return service;
    }

    /// <summary>
    /// The profile's session reads a snapshot with these tables – <c>A</c>, or <c>A:X,Y</c> with NUMBER columns in this
    /// order; each read returns the next list, the last one stays.
    /// </summary>
    private void Opens(ConnectionProfile profile, params string[][] tables)
    {
        var reads = new Queue<string[]>(tables.Length == 0 ? [[]] : tables);
        var reader = Substitute.For<ISchemaReader>();
        reader.ReadSnapshotAsync(Arg.Any<string>(), Arg.Any<IProgress<string>?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var names = reads.Count > 1 ? reads.Dequeue() : reads.Peek();
                return new SchemaSnapshot(call.Arg<string>(), DateTimeOffset.Now, names.Select(Table).ToList());
            });

        static ObjectSnapshot Table(string spec)
        {
            var parts = spec.Split(':');
            var columns = parts.Length > 1
                ? parts[1].Split(',').Select((c, i) => new ColumnInfo(c, "NUMBER", null, false, null, null, true, false, null, i + 1)).ToList()
                : [];
            return new ObjectSnapshot(parts[0], TableKind.Table, columns, [], []);
        }
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

        await service.SetOptionsAsync(reference: 1, columnOrder: true, onlyDifferences: false, kinds: [CompareKind.Column]);

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
    public async Task Filters_keep_the_matrix_and_only_reference_and_column_order_recompute_it()
    {
        _secrets.SetPassword(_dev.Id, "pw");
        _secrets.SetPassword(_test.Id, "pw");
        Opens(_dev);
        Opens(_test);
        var service = await ServiceAsync();
        SetSides(service, _dev.Id, _test.Id);
        await service.CompareAsync();
        var first = service.Result;

        await service.SetOptionsAsync(service.Current.Reference, service.Current.ColumnOrder, onlyDifferences: true, kinds: [CompareKind.Index]);
        Assert.Same(first, service.Result);

        await service.SetOptionsAsync(reference: 0, service.Current.ColumnOrder, service.Current.OnlyDifferences, service.Current.Kinds);
        Assert.NotSame(first, service.Result);
        Assert.Equal(0, service.Result!.Options.Reference);
    }

    [Fact]
    public async Task Chosen_objects_stay_over_a_new_comparison_until_they_are_alike_or_the_sides_change()
    {
        _secrets.SetPassword(_dev.Id, "pw");
        _secrets.SetPassword(_test.Id, "pw");
        Opens(_dev, ["A", "B", "C"]);
        Opens(_test, ["C"], ["A", "C"]); // the second read: A was created from the proposal
        var service = await ServiceAsync();
        SetSides(service, _dev.Id, _test.Id);
        await service.CompareAsync();
        var rows = service.Result!.Objects.ToDictionary(o => o.Name);

        Assert.Empty(service.Chosen); // nothing chosen at first
        service.Choose([rows["A"], rows["B"], rows["C"]], on: true);
        Assert.Equal(["A", "B"], service.ChosenRows.Select(r => r.Name)); // C is alike everywhere

        await service.CompareAsync();
        Assert.Equal(["B"], service.ChosenRows.Select(r => r.Name));

        service.Choose([service.Result!.Objects.Single(o => o.Name == "B")], on: false);
        Assert.Empty(service.Chosen);

        service.Choose(service.Result!.Objects, on: true);
        SetSides(service, _dev.Id, _test.Id);
        Assert.Empty(service.Chosen);
    }

    /// <summary>Review: a side that failed left a smaller matrix, and every object differing only against it lost its choice for good.</summary>
    [Fact]
    public async Task A_side_failing_on_comparing_again_keeps_the_choice_until_all_sides_load()
    {
        _secrets.SetPassword(_dev.Id, "pw");
        _secrets.SetPassword(_test.Id, "pw");
        Opens(_dev, ["A", "B", "C"]);
        Opens(_test, ["C"]);
        var service = await ServiceAsync();
        SetSides(service, _dev.Id, _test.Id);
        await service.CompareAsync();
        service.Choose(service.Result!.Objects, on: true);

        _connector.OpenAsync(_test, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new DatabaseException("TNS: Zeitüberschreitung", "ORA-12170"));
        await service.CompareAsync();

        Assert.Equal([0], service.ResultSides);
        Assert.Empty(service.ChosenRows); // one side: nothing differs, nothing to propose
        Assert.Equal(["A", "B"], service.Chosen.Order(StringComparer.Ordinal)); // but the choice waits

        Opens(_test, ["A", "C"]); // reachable again, A was created meanwhile
        await service.CompareAsync();

        Assert.Equal(["B"], service.ChosenRows.Select(r => r.Name));
        Assert.Equal(["B"], service.Chosen);
    }

    /// <summary>Review: waiting objects had no box of their own; taking all out left them to come back unasked.</summary>
    [Fact]
    public async Task Taking_all_out_also_drops_the_objects_waiting_for_a_missing_side()
    {
        _secrets.SetPassword(_dev.Id, "pw");
        _secrets.SetPassword(_test.Id, "pw");
        Opens(_dev, ["A", "B", "C"]);
        Opens(_test, ["C"]);
        var service = await ServiceAsync();
        SetSides(service, _dev.Id, _test.Id);
        await service.CompareAsync();
        service.Choose(service.Result!.Objects, on: true);
        _connector.OpenAsync(_test, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new DatabaseException("TNS: Zeitüberschreitung", "ORA-12170"));
        await service.CompareAsync();

        // Two sides, the target failed: nothing differs, yet the header's box stays usable and takes A and B out.
        var head = service.Header(service.Result!.Objects);
        Assert.Equal((true, true, false, false), (head.Enabled, head.Partly, head.Checked, head.Next));
        Assert.Equal(2, service.Waiting);
        Assert.StartsWith("Die 2 wartenden Objekte aus der Auswahl nehmen", head.Title, StringComparison.Ordinal);
        service.ChooseAll(head.Matches, head.Next);
        Assert.False(service.Header(service.Result!.Objects).Enabled);

        Opens(_test, ["C"]);
        await service.CompareAsync();

        Assert.Empty(service.Chosen);
        Assert.Empty(service.ChosenRows);
    }

    /// <summary>Review: column order off and on again cost the choice of objects differing only in their order.</summary>
    [Fact]
    public async Task Column_order_off_hides_the_choice_and_on_brings_it_back()
    {
        _secrets.SetPassword(_dev.Id, "pw");
        _secrets.SetPassword(_test.Id, "pw");
        Opens(_dev, ["A:X,Y", "B"]);
        Opens(_test, ["A:Y,X"]);
        var service = await ServiceAsync();
        SetSides(service, _dev.Id, _test.Id);
        await service.SetOptionsAsync(reference: null, columnOrder: true, onlyDifferences: false, kinds: null);
        await service.CompareAsync();
        service.Choose(service.Result!.Objects, on: true);
        Assert.Equal(["A", "B"], service.ChosenRows.Select(r => r.Name));

        await service.SetOptionsAsync(reference: null, columnOrder: false, onlyDifferences: false, kinds: null);
        Assert.Equal(["B"], service.ChosenRows.Select(r => r.Name)); // A is alike without the order and waits
        var head = service.Header(service.Result!.Objects);
        Assert.Equal((true, false, false), (head.Checked, head.Partly, head.Next));
        Assert.StartsWith("Den Treffer und das wartende Objekt aus der Auswahl nehmen", head.Title, StringComparison.Ordinal); // the waiting one is named before it goes

        await service.SetOptionsAsync(reference: null, columnOrder: true, onlyDifferences: false, kinds: null);
        Assert.Equal(["A", "B"], service.ChosenRows.Select(r => r.Name));
    }

    /// <summary>Compared from the UI thread, the sides load and the matrix builds without holding it (the window froze).</summary>
    [Fact]
    public async Task Comparing_from_the_ui_thread_leaves_it_free()
    {
        using var ui = new UiThread();
        _secrets.SetPassword(_dev.Id, "pw");
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = Substitute.For<ISchemaReader>();
        reader.ReadSnapshotAsync(Arg.Any<string>(), Arg.Any<IProgress<string>?>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            reading.TrySetResult();
            release.Task.Wait(Ct); // a synchronous read, as a driver may do it
            return new SchemaSnapshot(call.Arg<string>(), DateTimeOffset.Now, []);
        });
        var connection = FakeDatabase.Connection(reader);
        _connector.OpenAsync(_dev, Arg.Any<string>(), SchemaCompareLoader.SessionAction, Arg.Any<CancellationToken>()).Returns(connection);
        var service = await ServiceAsync();
        SetSides(service, _dev.Id);

        var comparing = ui.RunAsync(() => service.CompareAsync());
        await reading.Task.WaitAsync(Ct);
        await ui.RunAsync(() => Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(5), Ct); // the UI thread still answers
        release.SetResult();
        await comparing.WaitAsync(Ct);

        Assert.NotNull(service.Result);
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

        Assert.Equal("Kunde A › ERP Test · APP_USER", service.Label(new CompareSide(_test.Id)));
        Assert.Equal("Kunde A › ERP Test · ERP", service.Label(new CompareSide(_test.Id, "erp")));
        Assert.Equal("ERP Dev · APP_USER", service.Label(new CompareSide(_dev.Id)));
        Assert.Equal(CompareViewText.Compare_MissingConnection, service.Label(new CompareSide(Guid.NewGuid())));
    }

    [Fact]
    public async Task The_connections_to_choose_from_come_by_group_with_the_group_in_front()
    {
        var service = await ServiceAsync();

        Assert.Equal([_test.Id, _dev.Id], service.ChoicesIn(null).Select(p => p.Id));
        Assert.Equal("Kunde A › ERP Test", service.ChoiceLabel(_test));
        Assert.Equal("ERP Dev", service.ChoiceLabel(_dev));
    }

    [Fact]
    public async Task Without_the_group_setting_a_side_shows_the_bare_name()
    {
        var service = await ServiceAsync(AppSettings.Default with { ShowConnectionGroup = false });

        Assert.Equal("ERP Test", service.ChoiceLabel(_test));
        Assert.Equal("ERP Test · APP_USER", service.Label(new CompareSide(_test.Id)));
    }

    public void Dispose() => _folder.Dispose();
}
