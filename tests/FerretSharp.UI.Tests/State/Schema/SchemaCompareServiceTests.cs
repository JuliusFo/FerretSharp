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

    private Task<SchemaCompareService> ServiceAsync(AppSettings? settings = null) => ServiceAsync([], settings);

    private async Task<SchemaCompareService> ServiceAsync(IReadOnlyList<ConnectionProfile> more, AppSettings? settings = null)
    {
        var store = Substitute.For<IConnectionStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns((IReadOnlyList<ConnectionProfile>)[_dev, _test, .. more]);
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

    /// <summary>
    /// WP-83: the page is built anew on every switch of the shown connection – the search, the opened rows and "Show
    /// more" stay with the comparison, a new comparison of the same sides keeps the search and the rows, New clears them.
    /// </summary>
    [Fact]
    public async Task The_view_of_the_matrix_stays_with_the_comparison_and_New_clears_it()
    {
        _secrets.SetPassword(_dev.Id, "pw");
        _secrets.SetPassword(_test.Id, "pw");
        Opens(_dev, ["AUFTRAG", "KUNDE"]);
        Opens(_test, ["KUNDE"]);
        var service = await ServiceAsync();
        SetSides(service, _dev.Id, _test.Id);
        await service.CompareAsync();

        service.Search = "auf";
        service.Toggle("AUFTRAG");
        Assert.Equal(["AUFTRAG"], service.Visible().Select(r => r.Name));
        service.ShowMore();
        Assert.Equal(2 * SchemaCompareService.RowsPerStep, service.ShownRows);
        Assert.Equal(2 * SchemaCompareService.RowsPerStep, service.ShownRows); // same selection: the page asks again, nothing resets

        await service.CompareAsync();
        Assert.Equal(["AUFTRAG"], service.Visible().Select(r => r.Name));
        Assert.True(service.IsOpen("AUFTRAG"));
        Assert.Equal(SchemaCompareService.RowsPerStep, service.ShownRows); // a new matrix starts with the first rows

        service.New();
        Assert.Equal(("", false), (service.Search, service.IsOpen("AUFTRAG")));
    }

    /// <summary>
    /// WP-83: after a run only the target is read again; the matrix and the dialog stay meanwhile, what is aligned drops
    /// out of the choice, and success closes the run panel while the dialog stays with what is left.
    /// </summary>
    [Fact]
    public async Task A_run_on_the_target_reads_only_the_target_again_and_keeps_the_dialog()
    {
        _secrets.SetPassword(_dev.Id, "pw");
        _secrets.SetPassword(_test.Id, "pw");
        Opens(_dev, ["A", "B"]);
        Opens(_test, [], ["A"]); // the second read: the run created A, B failed
        var service = await ServiceAsync();
        SetSides(service, _dev.Id, _test.Id);
        await service.CompareAsync();
        service.Choose(service.Result!.Objects, on: true);
        service.OpenDdl();
        Assert.Equal(new DdlChoice(0, 1), service.Ddl);

        service.RequestRun(true);
        var failed = new DdlRunResult(["CREATE TABLE A"], "CREATE TABLE B", new DatabaseException("ORA-01031"), null) { Total = 2 };
        var reading = service.RanAsync(failed);
        Assert.NotNull(service.Result); // the old matrix stays while the target is read
        await reading;

        Assert.Equal(new DdlChoice(0, 1, Run: true, Failed: failed), service.Ddl); // the run panel stays with the failure
        Assert.Equal(["B"], service.ChosenRows.Select(r => r.Name));
        await _connector.Received(1).OpenAsync(_dev, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _connector.Received(2).OpenAsync(_test, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

        await service.RanAsync(new DdlRunResult([], null, null, null, NotDiscarded: true)); // nothing ran: nothing to read
        await _connector.Received(2).OpenAsync(_test, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

        await service.RanAsync(new DdlRunResult(["CREATE TABLE B"], null, null, null) { Total = 1 });
        Assert.Equal(new DdlChoice(0, 1), service.Ddl); // success: back to the proposal
    }

    /// <summary>
    /// Review WP-83: Esc and ✕ closed the dialog while its DDL ran – the run went on unseen, its target was never read
    /// again, and the next proposal offered what had run. The dialog stays while it runs; the target is read either way.
    /// </summary>
    [Fact]
    public async Task The_dialog_stays_while_its_ddl_runs_and_the_target_is_read_again_also_without_it()
    {
        _secrets.SetPassword(_dev.Id, "pw");
        _secrets.SetPassword(_test.Id, "pw");
        Opens(_dev, ["A"]);
        Opens(_test, [], ["A"]);
        var service = await ServiceAsync();
        SetSides(service, _dev.Id, _test.Id);
        await service.CompareAsync();
        service.OpenDdl();
        service.RequestRun(true);

        service.SetRunning(true);
        service.CloseDdl();
        service.RequestRun(false); // the panel's Close: a second run could start from the header
        Assert.True(service.DdlRunning);
        Assert.Equal(new DdlChoice(0, 1, Run: true), service.Ddl);

        service.SetRunning(false); // the panel's statements are over; its result comes next
        service.CloseDdl();
        Assert.Null(service.Ddl);
        await service.RanAsync(new DdlRunResult(["CREATE TABLE A"], null, null, null) { Total = 1 });

        await _connector.Received(2).OpenAsync(_test, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        Assert.False(service.Result!.Objects.Single().HasDifferences);
    }

    /// <summary>Review WP-83: reading the target again after a run and cancelling it left the snapshot of before the DDL, looking current.</summary>
    [Fact]
    public async Task Cancelling_a_side_read_again_drops_its_old_snapshot()
    {
        _secrets.SetPassword(_dev.Id, "pw");
        _secrets.SetPassword(_test.Id, "pw");
        Opens(_dev, ["A"]);
        Opens(_test, []);
        var service = await ServiceAsync();
        SetSides(service, _dev.Id, _test.Id);
        await service.CompareAsync();

        var opening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _connector.OpenAsync(_test, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                opening.TrySetResult();
                await Task.Delay(Timeout.Infinite, call.ArgAt<CancellationToken>(3));
                return FakeDatabase.Connection();
            });
        var reading = service.CompareSideAsync(1);
        await opening.Task.WaitAsync(Ct);
        Assert.NotNull(service.Sides[1].Snapshot); // kept while reading
        service.Cancel();
        await reading.WaitAsync(Ct);

        Assert.Null(service.Sides[1].Snapshot);
        Assert.Equal([0], service.ResultSides);
    }

    /// <summary>WP-83: a run asked for on one target never carries over to another – a side of the dialog failing starts it over.</summary>
    [Fact]
    public async Task The_dialog_keeps_its_sides_by_side_and_starts_over_when_one_of_them_fails()
    {
        var prod = TestApp.Profile("ERP Prod", ConnectionKind.Prod);
        _secrets.SetPassword(_dev.Id, "pw");
        _secrets.SetPassword(_test.Id, "pw");
        _secrets.SetPassword(prod.Id, "pw");
        Opens(_dev, ["A"]);
        Opens(_test, ["A"]);
        Opens(prod, []);
        var service = await ServiceAsync([prod]);
        SetSides(service, _dev.Id, _test.Id, prod.Id);
        await service.CompareAsync();
        service.OpenDdl();
        service.SetDdlColumns(1, 2);
        service.RequestRun(true);
        Assert.Equal((1, 2), service.DdlColumns());

        _connector.OpenAsync(_dev, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new DatabaseException("TNS: Zeitüberschreitung", "ORA-12170"));
        await service.CompareSideAsync(0);
        Assert.Equal(new DdlChoice(1, 2, Run: true), service.Ddl); // dev is no side of the dialog
        Assert.Equal((0, 1), service.DdlColumns()); // columns of the matrix without dev

        Opens(_dev, ["A"]); // dev back; then the dialog's target fails with two columns left
        await service.CompareSideAsync(0);
        _connector.OpenAsync(prod, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new DatabaseException("TNS: Zeitüberschreitung", "ORA-12170"));
        await service.CompareSideAsync(2);
        Assert.Equal(new DdlChoice(0, 1), service.Ddl); // the default pair, and the run asked for on prod is gone

        _connector.OpenAsync(_test, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new DatabaseException("TNS: Zeitüberschreitung", "ORA-12170"));
        await service.CompareSideAsync(1); // one column left: no dialog
        Assert.Null(service.Ddl);
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
