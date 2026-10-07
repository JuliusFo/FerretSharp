using FerretSharp.Core.Compare;
using FerretSharp.Core.Connections;
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
public sealed class SchemaCompareService(ComparisonStore store, SchemaCompareLoader loader, ConnectionManager connections, ILogger<SchemaCompareService> logger)
{
    private readonly Dictionary<Guid, string> _passwords = []; // typed for this session only, never saved
    private CancellationTokenSource? _cts;
    private bool _loaded;

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

    private static SavedComparison NewComparison() => new(Guid.NewGuid(), "", [new CompareSide(Guid.Empty), new CompareSide(Guid.Empty)]);

    /// <summary>Reads the saved comparisons once; a broken file is reported, not overwritten.</summary>
    public async Task EnsureLoadedAsync()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        try
        {
            Saved = await store.LoadAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StoreError = ex.Message;
        }

        if (Sides.Count == 0)
        {
            ResetSides();
        }

        Changed?.Invoke();
    }

    /// <summary>The label of a side for headers and the export: <c>ERP Test · ERP</c>.</summary>
    public string Label(CompareSide side) =>
        connections.Profiles.FirstOrDefault(p => p.Id == side.ConnectionId) is { } profile
            ? $"{profile.Name} · {side.OwnerFor(profile)}"
            : "(Verbindung fehlt)";

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

    /// <summary>Reference, column order and filters: the matrix is recomputed from the snapshots already read.</summary>
    public void SetOptions(int? reference, bool columnOrder, bool onlyDifferences, IReadOnlyList<CompareKind>? kinds)
    {
        Current = Current with { Reference = reference, ColumnOrder = columnOrder, OnlyDifferences = onlyDifferences, Kinds = kinds };
        Recompute();
        Changed?.Invoke();
    }

    /// <summary>A password typed for a connection without a stored one; kept until FerretSharp ends.</summary>
    public void SetPassword(Guid connectionId, string password) => _passwords[connectionId] = password;

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

        Result = null;
        Changed?.Invoke();
        try
        {
            var results = await loader.LoadAsync(targets.Select(t => t.state.Side).ToList(), _passwords,
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

        Recompute();
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
        try
        {
            await store.SaveAsync(saved, CancellationToken.None);
            Saved = saved;
            StoreError = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StoreError = $"Speichern fehlgeschlagen: {ex.Message}";
        }

        Changed?.Invoke();
    }

    private void ResetSides()
    {
        Sides = Current.Sides.Select(side => new SideState(side)).ToList();
        Result = null;
        ResultSides = [];
        Changed?.Invoke();
    }

    /// <summary>The matrix over the sides that loaded; the reference only if it loaded too.</summary>
    private void Recompute()
    {
        var loaded = Sides.Select((state, index) => (state, index)).Where(s => s.state.Snapshot is not null).ToList();
        if (loaded.Count == 0)
        {
            (Result, ResultSides) = (null, []);
            return;
        }

        var reference = Current.Reference is { } r ? loaded.FindIndex(s => s.index == r) : -1;
        ResultSides = loaded.Select(s => s.index).ToList();
        Result = SchemaDiff.Compare(loaded.Select(s => s.state.Snapshot!).ToList(), new CompareOptions(reference >= 0 ? reference : null, Current.ColumnOrder));
    }
}
