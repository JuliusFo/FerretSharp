using System.Diagnostics;
using FerretSharp.Core.Connections;
using FerretSharp.Core.Schema;

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
/// <param name="CachedAt">The model came from the cache (WP-16): when the model host exported it; null if it ran now.</param>
/// <param name="BuildChanged">While loading: the load was started because the build output changed (ADR 0016).</param>
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
    IReadOnlyList<LoadStep>? Steps = null,
    DateTimeOffset? CachedAt = null,
    bool BuildChanged = false)
{
    public static readonly ClrModelState None = new(ClrModelPhase.None, null, null, null, null, null);
}

/// <summary>One step of loading the model and how long it took (shown on the model page).</summary>
public sealed record LoadStep(string Name, TimeSpan Duration);

/// <summary>
/// The C# model of the active connection's linked project (WP-11): loaded in the background after connecting, again on
/// request ("Neu laden", after "Neu bauen"), when the link changes and when a build changed the output (ADR 0016: a
/// watcher on the output folder; the previous model stays usable meanwhile). Nothing of it touches the database except
/// reading column lists through the schema cache.
/// </summary>
public sealed class ClrModelManager : IDisposable
{
    private readonly IModelHostRunner _runner;
    private readonly ActiveConnection _active;
    private readonly ConnectionManager _connections;
    private readonly ModelCache? _cache;
    private readonly Lock _lock = new();
    private readonly TimeSpan _buildQuiet;
    private CancellationTokenSource? _loading;
    private Guid? _profileId;
    private BuildOutputWatcher? _watcher;

    /// <summary>The files of the build output the current model was read from (to ignore watcher events without a change).</summary>
    private IReadOnlyDictionary<string, FileStamp>? _loadedFiles;

    /// <param name="cache">Exported models to reuse while the build is unchanged (WP-16); null = always run the host.</param>
    /// <param name="buildQuiet">How long the build output must be unchanged before the model is reloaded.</param>
    public ClrModelManager(IModelHostRunner runner, ActiveConnection active, ConnectionManager connections, ModelCache? cache = null, TimeSpan? buildQuiet = null)
    {
        _runner = runner;
        _active = active;
        _connections = connections;
        _cache = cache;
        _buildQuiet = buildQuiet ?? BuildOutputWatcher.DefaultQuiet;
        _active.Changed += OnConnectionChanged;
        _connections.Changed += OnConnectionChanged;
    }

    /// <summary>May fire on a background thread.</summary>
    public event Action? Changed;

    /// <summary>
    /// A build changed the output of the linked project (after the build finished writing); the model reloads. Fires on a
    /// background thread; the LINQ console restarts once that load has ended.
    /// </summary>
    public event Action? BuildOutputChanged;

    public ClrModelState State { get; private set; } = ClrModelState.None;

    public bool IsBuilding { get; private set; }

    /// <summary>Output of the last "Neu bauen".</summary>
    public DotNetRun? LastBuild { get; private set; }

    /// <summary>The mapping if a model is loaded (also while it reloads).</summary>
    public ClrModelMapping? Mapping => State.Mapping;

    /// <summary>The link of the active connection as currently saved (the dialog may have changed it).</summary>
    private ClrProjectLink? CurrentLink =>
        _active.Profile is { } profile ? (_connections.Profiles.FirstOrDefault(p => p.Id == profile.Id) ?? profile).ClrProject : null;

    /// <summary>Reads the model again from the project ("Neu laden"), not from the cache; a load still running is cancelled.</summary>
    public Task LoadAsync() => LoadAsync(useCache: false, buildChanged: false);

    /// <param name="useCache">Take the cached model if the build output is unchanged (connecting, link changed, new build).</param>
    /// <param name="buildChanged">Started by the watcher: the status bar says why the model reloads.</param>
    private Task LoadAsync(bool useCache, bool buildChanged)
    {
        CancellationTokenSource cts;
        lock (_lock)
        {
            _loading?.Cancel();
            _loading = cts = new CancellationTokenSource();
        }

        // Entirely off the caller's thread: mapping a large model (hundreds of entities) must not stall the UI.
        return Task.Run(() => LoadAsync(useCache, buildChanged, cts));
    }

    private async Task LoadAsync(bool useCache, bool buildChanged, CancellationTokenSource cts)
    {
        var link = CurrentLink;
        var schema = _active.Schema;
        if (link is null || schema is null || !_active.IsConnected)
        {
            schema?.SetForeignKeys(FkSource.ClrModel, []); // unlinked: the model's relationships go too
            Watch(null, null, cts);
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
                step, DateTimeOffset.Now, steps.StartedAt, BuildChanged: buildChanged), cts);
        }

        DateTimeOffset? cachedAt = null;

        void Finish(ClrModelPhase phase, ClrModelMapping? mapping, ModelHostError? error, DateTimeOffset? at) =>
            Set(new ClrModelState(phase, link, output, mapping, error, at, Steps: steps.Finish(), CachedAt: cachedAt), cts);

        try
        {
            Report("Suche den Build");
            try
            {
                output = BuildOutputLocator.Find(link);
            }
            finally
            {
                // Also without a build: the first one is noticed.
                Watch(link, output, cts);
            }

            CachedModel? cached = null;
            if (useCache && _cache is not null)
            {
                Report("Prüfe den Cache");
                cached = await _cache.TryLoadAsync(link, output, cts.Token);
            }

            ModelExport model;
            if (cached is not null)
            {
                model = cached.Model;
                cachedAt = cached.ExportedAt;
            }
            else
            {
                var result = await _runner.ReadModelAsync(link, output, cts.Token, new Reporter(Report));
                if (result.Model is not { } exported)
                {
                    Finish(ClrModelPhase.Failed, previous, result.Error ?? new ModelHostError(ClrModelErrorKind.HostFailed, "Kein Modell geliefert."), loadedAt);
                    return;
                }

                model = exported;
                if (_cache is not null)
                {
                    await _cache.SaveAsync(link, output, model, cts.Token);
                }
            }

            var mapping = await ClrModelMapping.BuildAsync(model, schema, cts.Token, new Reporter(Report));
            cts.Token.ThrowIfCancellationRequested();
            schema.SetForeignKeys(FkSource.ClrModel, mapping.ForeignKeys);
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
        catch (Exception ex)
        {
            // Runs in the background (nobody awaits it): anything unexpected – a deps.json rewritten by a build running right
            // now, a truncated result file – must end in "Failed", never leave the state at "Loading".
            Finish(ClrModelPhase.Failed, previous, new ModelHostError(ClrModelErrorKind.HostFailed, $"Das Modell ließ sich nicht laden: {ex.Message}"), loadedAt);
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
        catch (Exception ex) when (ex is ClrModelException or IOException or System.ComponentModel.Win32Exception)
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
        Watch(null, null, owner: null);
    }

    /// <summary>
    /// Watches the output folder of the build being read (or the project folder until there is one); remembers its files
    /// to tell a real change from events without one. Null link: stops watching.
    /// </summary>
    /// <param name="owner">The load asking; a load superseded meanwhile changes nothing. Null: always (dispose).</param>
    private void Watch(ClrProjectLink? link, BuildOutput? output, CancellationTokenSource? owner)
    {
        var (directory, depsOnly) = link is null ? (null, false)
            : output is not null ? (Path.GetDirectoryName(Path.GetFullPath(output.DepsFile)), false)
            : (Path.GetDirectoryName(Path.GetFullPath(link.ProjectFile)), true);
        IReadOnlyDictionary<string, FileStamp>? files = null;
        if (directory is not null && !depsOnly)
        {
            try
            {
                files = BuildFiles.List(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // a build deleting the folder right now: the next event reloads anyway
            }
        }

        BuildOutputWatcher? old = null;
        lock (_lock)
        {
            if (owner is not null && !ReferenceEquals(_loading, owner))
            {
                return;
            }

            _loadedFiles = files;
            if (_watcher is { } current && current.Directory == directory && current.DepsOnly == depsOnly)
            {
                return;
            }

            old = _watcher;
            _watcher = null;
            if (directory is not null && Directory.Exists(directory))
            {
                try
                {
                    _watcher = new BuildOutputWatcher(directory, depsOnly, _buildQuiet, () => OnBuildOutputChanged(link!));
                }
                catch (Exception ex) when (ex is IOException or ArgumentException or PlatformNotSupportedException)
                {
                    // no watcher: "Neu laden" and the console's own check still see a new build
                }
            }
        }

        old?.Dispose();
    }

    /// <summary>The watcher saw a finished build: reload the model (from the cache if the output is the same after all).</summary>
    private void OnBuildOutputChanged(ClrProjectLink link)
    {
        if (!_active.IsConnected || CurrentLink != link)
        {
            return;
        }

        BuildOutputWatcher? watcher;
        IReadOnlyDictionary<string, FileStamp>? loaded;
        lock (_lock)
        {
            (watcher, loaded) = (_watcher, _loadedFiles);
        }

        if (watcher is { DepsOnly: false } && loaded is not null)
        {
            try
            {
                if (BuildFiles.Same(loaded, BuildFiles.List(watcher.Directory)))
                {
                    return; // touched, not changed (e.g. an up-to-date build)
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // changing right now: reload
            }
        }

        BuildOutputChanged?.Invoke();
        _ = LoadAsync(useCache: true, buildChanged: true);
    }

    /// <summary>Connected (to another profile), disconnected, or the link of the active profile changed.</summary>
    private void OnConnectionChanged()
    {
        var connected = _active.IsConnected ? _active.Profile?.Id : null;
        var link = connected is null ? null : CurrentLink;
        if (connected != _profileId || link != State.Link)
        {
            _profileId = connected;
            _ = LoadAsync(useCache: true, buildChanged: false);
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
