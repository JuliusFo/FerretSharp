using System.Diagnostics;
using FerretSharp.Core.Connections;

namespace FerretSharp.Core.ClrModel;

public enum ClrModelPhase
{
    /// <summary>No project linked (or not connected).</summary>
    None,
    Loading,
    Loaded,
    Failed,
}

/// <param name="Output">The build output read (also on a failure after it was found).</param>
/// <param name="Mapping">The model laid over the schema; kept while a reload runs.</param>
/// <param name="Step">While loading: what is being done right now, since <paramref name="StepStartedAt"/>.</param>
/// <param name="Steps">The steps of the last load with their durations (also of a failed one).</param>
public sealed record ClrModelState(
    ClrModelPhase Phase,
    ClrProjectLink? Link,
    BuildOutput? Output,
    ClrModelMapping? Mapping,
    ModelHostError? Error,
    DateTimeOffset? LoadedAt,
    string? Step = null,
    DateTimeOffset? StepStartedAt = null,
    DateTimeOffset? LoadStartedAt = null,
    IReadOnlyList<LoadStep>? Steps = null)
{
    public static readonly ClrModelState None = new(ClrModelPhase.None, null, null, null, null, null);
}

/// <summary>One step of loading the model and how long it took (shown on the model page).</summary>
public sealed record LoadStep(string Name, TimeSpan Duration);

/// <summary>
/// The C# model of the active connection's linked project (WP-11): loaded in the background after connecting, again on
/// request ("Neu laden", after "Neu bauen") and when the link changes. Nothing of it touches the database except reading
/// column lists through the schema cache.
/// </summary>
public sealed class ClrModelManager : IDisposable
{
    private readonly IModelHostRunner _runner;
    private readonly ActiveConnection _active;
    private readonly ConnectionManager _connections;
    private readonly Lock _lock = new();
    private CancellationTokenSource? _loading;
    private Guid? _profileId;

    public ClrModelManager(IModelHostRunner runner, ActiveConnection active, ConnectionManager connections)
    {
        _runner = runner;
        _active = active;
        _connections = connections;
        _active.Changed += OnConnectionChanged;
        _connections.Changed += OnConnectionChanged;
    }

    /// <summary>May fire on a background thread.</summary>
    public event Action? Changed;

    public ClrModelState State { get; private set; } = ClrModelState.None;

    public bool IsBuilding { get; private set; }

    /// <summary>Output of the last "Neu bauen".</summary>
    public DotNetRun? LastBuild { get; private set; }

    /// <summary>The mapping if a model is loaded (also while it reloads).</summary>
    public ClrModelMapping? Mapping => State.Mapping;

    /// <summary>The link of the active connection as currently saved (the dialog may have changed it).</summary>
    private ClrProjectLink? CurrentLink =>
        _active.Profile is { } profile ? (_connections.Profiles.FirstOrDefault(p => p.Id == profile.Id) ?? profile).ClrProject : null;

    /// <summary>Reads the model again; a load still running is cancelled.</summary>
    public async Task LoadAsync()
    {
        CancellationTokenSource cts;
        lock (_lock)
        {
            _loading?.Cancel();
            _loading = cts = new CancellationTokenSource();
        }

        var link = CurrentLink;
        var schema = _active.Schema;
        if (link is null || schema is null || !_active.IsConnected)
        {
            Set(ClrModelState.None, cts);
            return;
        }

        var previous = State.Link == link ? State.Mapping : null;
        var loadedAt = State.LoadedAt;
        var steps = new StepRecorder();
        BuildOutput? output = null;

        // Progress: the step (also those the model host reports from its process) with its start, for the model page.
        void Report(string step)
        {
            steps.Start(step);
            Set(new ClrModelState(ClrModelPhase.Loading, link, output ?? State.Output, previous, null, loadedAt,
                step, DateTimeOffset.Now, steps.StartedAt), cts);
        }

        void Finish(ClrModelPhase phase, ClrModelMapping? mapping, ModelHostError? error, DateTimeOffset? at) =>
            Set(new ClrModelState(phase, link, output, mapping, error, at, Steps: steps.Finish()), cts);

        try
        {
            Report("Suche den Build");
            output = await Task.Run(() => BuildOutputLocator.Find(link), cts.Token);
            var result = await Task.Run(() => _runner.ReadModelAsync(link, output, cts.Token, new Reporter(Report)), cts.Token);
            if (result.Model is not { } model)
            {
                Finish(ClrModelPhase.Failed, previous, result.Error ?? new ModelHostError(ClrModelErrorKind.HostFailed, "Kein Modell geliefert."), loadedAt);
                return;
            }

            var mapping = await ClrModelMapping.BuildAsync(model, schema, cts.Token, new Reporter(Report));
            Finish(ClrModelPhase.Loaded, mapping, null, DateTimeOffset.Now);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // superseded by a newer load or the connection ended
        }
        catch (ClrModelException ex)
        {
            Finish(ClrModelPhase.Failed, previous, ex.ToError(), loadedAt);
        }
        catch (DatabaseException ex)
        {
            Finish(ClrModelPhase.Failed, previous, new ModelHostError(ClrModelErrorKind.HostFailed, $"Spalten ließen sich nicht lesen: {ex.Display}"), loadedAt);
        }
    }

    /// <summary>Durations of the steps of one load: a step lasts until the next one starts.</summary>
    private sealed class StepRecorder
    {
        private readonly List<LoadStep> _steps = [];
        private readonly Stopwatch _watch = new();
        private string? _current;

        public DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;

        public void Start(string step)
        {
            lock (_steps)
            {
                Close();
                _current = step;
                _watch.Restart();
            }
        }

        public IReadOnlyList<LoadStep> Finish()
        {
            lock (_steps)
            {
                Close();
                return _steps.ToList();
            }
        }

        private void Close()
        {
            if (_current is { } step)
            {
                _steps.Add(new LoadStep(step, _watch.Elapsed));
                _current = null;
            }
        }
    }

    /// <summary>Calls back on whatever thread reports (unlike <see cref="Progress{T}"/>, which posts to a captured context).</summary>
    private sealed class Reporter(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }

    /// <summary><c>dotnet build</c> of the linked project, then reads the model again if the build succeeded.</summary>
    public async Task<DotNetRun?> BuildAsync()
    {
        if (CurrentLink is not { } link || IsBuilding)
        {
            return null;
        }

        IsBuilding = true;
        Changed?.Invoke();
        try
        {
            LastBuild = await Task.Run(() => _runner.BuildAsync(link, CancellationToken.None));
        }
        catch (ClrModelException ex)
        {
            LastBuild = new DotNetRun(-1, ex.Message);
        }
        finally
        {
            IsBuilding = false;
            Changed?.Invoke();
        }

        if (LastBuild.Succeeded)
        {
            await LoadAsync();
        }

        return LastBuild;
    }

    public void Dispose()
    {
        _active.Changed -= OnConnectionChanged;
        _connections.Changed -= OnConnectionChanged;
        _loading?.Cancel();
    }

    /// <summary>Connected (to another profile), disconnected, or the link of the active profile changed.</summary>
    private void OnConnectionChanged()
    {
        var connected = _active.IsConnected ? _active.Profile?.Id : null;
        var link = connected is null ? null : CurrentLink;
        if (connected != _profileId || link != State.Link)
        {
            _profileId = connected;
            _ = LoadAsync();
        }
    }

    private void Set(ClrModelState state, CancellationTokenSource owner)
    {
        lock (_lock)
        {
            if (!ReferenceEquals(_loading, owner))
            {
                return; // a newer load owns the state
            }

            State = state;
        }

        Changed?.Invoke();
    }
}
