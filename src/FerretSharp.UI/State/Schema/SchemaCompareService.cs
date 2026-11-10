using FerretSharp.Core.Compare;
using FerretSharp.Core.Connections;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Settings;
using FerretSharp.UI.Resources;
using Microsoft.Extensions.Logging;

namespace FerretSharp.UI.State;

/// <summary>How far a side of the comparison is: its step while loading, then its snapshot or why there is none.</summary>
public sealed class SideState(CompareSide side)
{
    public CompareSide Side { get; } = side;

    public bool Loading { get; set; }

    /// <summary>The step while loading (<c>Verbinde</c>, <c>Spalten</c> …).</summary>
    public string? Step { get; set; }

    public SchemaSnapshot? Snapshot { get; set; }

    public DatabaseException? Error { get; set; }

    public bool NeedsPassword { get; set; }
}

/// <summary>
/// The schema comparison page's state (WP-20), kept while the user visits other pages: the comparison being edited,
/// its sides with their snapshots, the matrix and the saved comparisons. Snapshots stay until the sides change or the
/// user compares again; changing the reference or the column order only recomputes the matrix.
/// </summary>
public sealed class SchemaCompareService(
    ComparisonStore store, SchemaCompareLoader loader, ConnectionManager connections, AppSettingsService settings, ILogger<SchemaCompareService> logger)
{
    private CancellationTokenSource? _cts;
    private bool _loaded;

    /// <summary>Counts matrix computations; a computation whose number is no longer the latest drops its result.</summary>
    private int _matrix;

    /// <summary>Raised on every change; may fire on a background thread.</summary>
    public event Action? Changed;

    public IReadOnlyList<SavedComparison> Saved { get; private set; } = [];

    /// <summary>Why the saved comparisons could not be read or written; null if fine.</summary>
    public string? StoreError { get; private set; }

    /// <summary>The comparison on the page; its <see cref="SavedComparison.Id"/> is in <see cref="Saved"/> once saved.</summary>
    public SavedComparison Current { get; private set; } = NewComparison();

    public bool IsSaved => Saved.Any(s => s.Id == Current.Id);

    public IReadOnlyList<SideState> Sides { get; private set; } = [];

    public bool Loading => _cts is not null;

    /// <summary>The matrix over the loaded sides; null before comparing or while fewer than one side is loaded.</summary>
    public SchemaComparison? Result { get; private set; }

    /// <summary>Indexes into <see cref="Sides"/> of the matrix columns (sides that failed are left out).</summary>
    public IReadOnlyList<int> ResultSides { get; private set; } = [];

    private readonly HashSet<string> _chosen = new(StringComparer.Ordinal);

    /// <summary>
    /// Keys of the objects chosen in the matrix for the DDL proposal: none at first, kept while filters hide them and over
    /// a new comparison of the same sides (objects no longer there or alike everywhere drop out once all sides loaded),
    /// not saved. What counts for the matrix is <see cref="IsChosen"/>.
    /// </summary>
    public IReadOnlySet<string> Chosen => _chosen;

    /// <summary>The chosen objects of the matrix in its order; what the DDL proposal covers.</summary>
    public IReadOnlyList<CompareRow> ChosenRows => Result is { } result ? result.Objects.Where(IsChosen).ToList() : [];

    /// <summary>Only an object that differs somewhere can be chosen: one alike on all sides never gets a step.</summary>
    public static bool CanChoose(CompareRow row) => row.HasDifferences;

    /// <summary>Chosen and differing in this matrix; a key that is alike here (a side missing, column order off) waits.</summary>
    public bool IsChosen(CompareRow row) => CanChoose(row) && _chosen.Contains(row.Key);

    /// <summary>Chooses objects for the DDL proposal or takes them out; rows that cannot be chosen are left alone.</summary>
    public void Choose(IEnumerable<CompareRow> rows, bool on)
    {
        foreach (var row in rows.Where(CanChoose))
        {
            if (on)
            {
                _chosen.Add(row.Key);
            }
            else
            {
                _chosen.Remove(row.Key);
            }
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// The header's box over the matches of the filters: chooses them, or takes them out together with the objects that
    /// only wait (alike in this matrix while a side is missing or column order is off) – those have no box of their own
    /// and would come back unasked once all sides load again (review).
    /// </summary>
    public void ChooseAll(IReadOnlyList<CompareRow> matches, bool on)
    {
        if (!on && Result is { } result)
        {
            var differing = result.Objects.Where(CanChoose).Select(o => o.Key).ToHashSet(StringComparer.Ordinal);
            _chosen.RemoveWhere(key => !differing.Contains(key));
        }

        Choose(matches, on);
    }

    private static SavedComparison NewComparison() => new(Guid.NewGuid(), "", [new CompareSide(Guid.Empty), new CompareSide(Guid.Empty)]);

    /// <summary>
    /// Reads the saved comparisons once they could be read; a file that could not be read is reported and read again on the
    /// next visit or save – never overwritten (review 3.40.0, ST-8: the next save replaced all saved comparisons with the
    /// new one, as the filter presets never did).
    /// </summary>
    public async Task EnsureLoadedAsync()
    {
        if (!_loaded)
        {
            await ReadAsync();
        }

        if (Sides.Count == 0)
        {
            ResetSides();
        }

        Changed?.Invoke();
    }

    /// <summary>Reads the file; <see cref="StoreError"/> says why not.</summary>
    private async Task ReadAsync()
    {
        try
        {
            Saved = await store.LoadAsync(CancellationToken.None);
            _loaded = true;
            StoreError = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StoreError = ex.Message;
        }
    }

    /// <summary>
    /// The label of a side for headers and the export: <c>Kunde A › ERP Test · ERP</c> – with the group, so two groups'
    /// connections of the same name stay apart.
    /// </summary>
    public string Label(CompareSide side) =>
        connections.Profiles.FirstOrDefault(p => p.Id == side.ConnectionId) is { } profile
            ? $"{ConnectionLabel.Of(profile, settings.Current)} · {side.OwnerFor(profile)}"
            : CompareViewText.Compare_MissingConnection;

    /// <summary>
    /// The connections to choose a side from, in the order of the switcher: by group in the user's order (WP-66; none:
    /// alphabetical, no group last), kind, name.
    /// </summary>
    public IReadOnlyList<ConnectionProfile> ChoicesIn(IReadOnlyList<string>? order) => ConnectionSearch.InListOrder(connections.Profiles, order);

    /// <summary>A connection in the side's list: <c>Kunde A › ERP Test</c>.</summary>
    public string ChoiceLabel(ConnectionProfile profile) => ConnectionLabel.Of(profile, settings.Current);

    public void New()
    {
        Cancel();
        Current = NewComparison();
        ResetSides();
    }

    public void Open(SavedComparison saved)
    {
        Cancel();
        Current = saved;
        ResetSides();
    }

    /// <summary>Changes the sides (connection, schema, added, removed): the snapshots no longer fit.</summary>
    public void SetSides(IReadOnlyList<CompareSide> sides, int? reference)
    {
        Cancel();
        Current = Current with { Sides = sides, Reference = reference is { } r && r < sides.Count ? r : null };
        ResetSides();
    }

    /// <summary>
    /// Reference, column order and filters. The first two recompute the matrix from the snapshots already read; the
    /// filters only change which rows the page shows.
    /// </summary>
    public async Task SetOptionsAsync(int? reference, bool columnOrder, bool onlyDifferences, IReadOnlyList<CompareKind>? kinds)
    {
        var recompute = reference != Current.Reference || columnOrder != Current.ColumnOrder;
        Current = Current with { Reference = reference, ColumnOrder = columnOrder, OnlyDifferences = onlyDifferences, Kinds = kinds };
        Changed?.Invoke();
        if (recompute)
        {
            await RecomputeAsync();
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// A password typed for a connection without a stored one; kept until FerretSharp ends – the same one connecting uses
    /// (review 3.31.0, A12: the comparison kept its own, and a mistyped one stayed until restart).
    /// </summary>
    public void SetPassword(Guid connectionId, string password) => connections.SetPassword(connectionId, password, remember: false);

    /// <summary>Reads all sides (or only those that failed for want of a password) and builds the matrix.</summary>
    public async Task CompareAsync(bool onlyMissing = false)
    {
        Cancel();
        var targets = Sides.Select((state, index) => (state, index))
            .Where(s => s.state.Side.ConnectionId != Guid.Empty && (!onlyMissing || s.state.Snapshot is null))
            .ToList();
        if (targets.Count == 0)
        {
            return;
        }

        using var cts = new CancellationTokenSource();
        _cts = cts;
        foreach (var (state, _) in targets)
        {
            (state.Loading, state.Step, state.Snapshot, state.Error, state.NeedsPassword) = (true, null, null, null, false);
        }

        Interlocked.Increment(ref _matrix); // a matrix still being computed from the old snapshots is not wanted
        Result = null;
        Changed?.Invoke();
        try
        {
            var results = await loader.LoadAsync(targets.Select(t => t.state.Side).ToList(),
                (index, step) =>
                {
                    targets[index].state.Step = step;
                    Changed?.Invoke();
                }, cts.Token);
            for (var i = 0; i < targets.Count; i++)
            {
                var (state, result) = (targets[i].state, results[i]);
                (state.Snapshot, state.Error, state.NeedsPassword) = (result.Snapshot, result.Error, result.NeedsPassword);
                if (result.Error is { } error && !result.NeedsPassword)
                {
                    logger.LogWarning("Schema comparison: {Side} failed: {Error}", Label(state.Side), error.Message);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // cancelled by the user or by changing the sides: the sides show nothing
        }
        finally
        {
            foreach (var (state, _) in targets)
            {
                (state.Loading, state.Step) = (false, null);
            }

            if (ReferenceEquals(_cts, cts))
            {
                _cts = null;
            }
        }

        if (_cts is not null)
        {
            return; // cancelled by a newer comparison, which builds the matrix when it ends
        }

        await RecomputeAsync(afterRead: true);
        Changed?.Invoke();
    }

    public void Cancel()
    {
        _cts?.Cancel();
        _cts = null;
    }

    /// <summary>Saves the current comparison under <paramref name="name"/> (a new one if <paramref name="asNew"/>).</summary>
    public async Task SaveAsync(string name, bool asNew)
    {
        Current = Current with { Id = asNew || !IsSaved ? Guid.NewGuid() : Current.Id, Name = name.Trim() };
        var saved = Saved.Where(s => s.Id != Current.Id).Append(Current).OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        await WriteAsync(saved);
    }

    public async Task DeleteAsync(Guid id)
    {
        await WriteAsync(Saved.Where(s => s.Id != id).ToList());
        if (Current.Id == id)
        {
            Current = Current with { Id = Guid.NewGuid() }; // stays on the page, now unsaved
        }

        Changed?.Invoke();
    }

    private async Task WriteAsync(IReadOnlyList<SavedComparison> saved)
    {
        if (!_loaded)
        {
            // What is in the file is not known: writing would replace every saved comparison with this list (ST-8).
            await ReadAsync();
            if (!_loaded)
            {
                StoreError = TextFormat.Format(CompareViewText.Compare_NotSavedUnread, StoreError);
                Changed?.Invoke();
                return;
            }

            // Readable now: what the file holds stays, with this change on top.
            saved = Saved.Where(s => saved.All(n => n.Id != s.Id)).Concat(saved).OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        try
        {
            await store.SaveAsync(saved, CancellationToken.None);
            Saved = saved;
            StoreError = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StoreError = TextFormat.Format(CommonText.SaveFailed, ex.Message);
        }

        Changed?.Invoke();
    }

    private void ResetSides()
    {
        Interlocked.Increment(ref _matrix);
        Sides = Current.Sides.Select(side => new SideState(side)).ToList();
        Result = null;
        ResultSides = [];
        _chosen.Clear(); // other sides, other objects
        Changed?.Invoke();
    }

    /// <summary>
    /// The matrix over the sides that loaded; the reference only if it loaded too. Computed on the thread pool – over
    /// large schemas the diff took long enough to freeze the window – and dropped if the sides or options changed meanwhile.
    /// </summary>
    /// <param name="afterRead">
    /// The sides were just read: if all of them loaded, chosen objects that are gone or alike everywhere drop out. Not
    /// after a side failed (an object that differs only against it would be lost, review) and not for a new matrix from
    /// the same snapshots (column order on and off) – there <see cref="IsChosen"/> only hides them.
    /// </param>
    private async Task RecomputeAsync(bool afterRead = false)
    {
        var version = Interlocked.Increment(ref _matrix);
        var loaded = Sides.Select((state, index) => (state, index)).Where(s => s.state.Snapshot is not null).ToList();
        if (loaded.Count == 0)
        {
            (Result, ResultSides) = (null, []);
            return;
        }

        var reference = Current.Reference is { } r ? loaded.FindIndex(s => s.index == r) : -1;
        var snapshots = loaded.Select(s => s.state.Snapshot!).ToList();
        var options = new CompareOptions(reference >= 0 ? reference : null, Current.ColumnOrder);
        var result = await Task.Run(() => SchemaDiff.Compare(snapshots, options));
        if (version == Volatile.Read(ref _matrix))
        {
            (Result, ResultSides) = (result, loaded.Select(s => s.index).ToList());
            if (afterRead && loaded.Count == Sides.Count)
            {
                // e.g. compared again after running the script: aligned objects drop out
                _chosen.IntersectWith(result.Objects.Where(CanChoose).Select(o => o.Key));
            }
        }
    }
}
