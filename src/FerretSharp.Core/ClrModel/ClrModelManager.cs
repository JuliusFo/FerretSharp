using System.Diagnostics;
using FerretSharp.Core.Connections;
using FerretSharp.Core.Resources;
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
/// <param name="ModelFile">The linked model file (WP-64) instead of a project (<paramref name="Link"/> is then null).</param>
/// <param name="Origin">Where the loaded model file comes from; null until one is read.</param>
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
    bool BuildChanged = false,
    string? ModelFile = null,
    ModelFileOrigin? Origin = null)
{
    public static readonly ClrModelState None = new(ClrModelPhase.None, null, null, null, null, null);

    /// <summary>A model is linked: a project or a model file. LINQ console, code usage and builds need <see cref="Link"/>.</summary>
    public bool IsLinked => Link is not null || ModelFile is not null;
}

/// <summary>One step of loading the model and how long it took (shown on the model page).</summary>
public sealed record LoadStep(string Name, TimeSpan Duration);

/// <summary>
/// The C# model of the active connection's linked project (WP-11): loaded in the background after connecting, again on
/// request ("Neu laden", after "Neu bauen"), when the link changes and when a build changed the output (ADR 0016: a
/// watcher on the output folder; the previous model stays usable meanwhile). Instead of a project a model file can be
/// linked (WP-64): read without build, cache or model host, again when the file changes. Nothing of it touches the
/// database except reading column lists through the schema cache.
/// </summary>
public sealed class ClrModelManager : IDisposable
{
    private readonly IModelHostRunner _runner;
    private readonly ActiveConnection _active;
    private readonly ConnectionManager _connections;
    private readonly ModelCache? _cache;
    private readonly Lock _lock = new();
    private readonly TimeSpan _buildQuiet;
    private readonly TimeProvider _time;
    private CancellationTokenSource? _loading;

    /// <summary>Cancelled on dispose (connection closed): a running build is ended instead of going on for up to ten minutes.</summary>
    private readonly CancellationTokenSource _lifetime = new();
    private Guid? _profileId;
    private BuildOutputWatcher? _watcher;

    /// <summary>File events the build watcher handled and the quiet periods it reported so far; 0 without a watcher (tests).</summary>
    internal (int Seen, int Reported) WatcherCounts
    {
        get
        {
            lock (_lock)
            {
                return (_watcher?.Seen ?? 0, _watcher?.Reported ?? 0);
            }
        }
    }

    /// <summary>The files of the build output the current model was read from (to ignore watcher events without a change).</summary>
    private IReadOnlyDictionary<string, FileStamp>? _loadedFiles;

    /// <param name="cache">Exported models to reuse while the build is unchanged (WP-16); null = always run the host.</param>
    /// <param name="buildQuiet">How long the build output must be unchanged before the model is reloaded.</param>
    /// <param name="time">The clock of the build watcher's quiet period and of the load times (tests); the system's if null.</param>
    public ClrModelManager(IModelHostRunner runner, ActiveConnection active, ConnectionManager connections, ModelCache? cache = null, TimeSpan? buildQuiet = null,
        TimeProvider? time = null)
    {
        _runner = runner;
        _active = active;
        _connections = connections;
        _cache = cache;
        _buildQuiet = buildQuiet ?? BuildOutputWatcher.DefaultQuiet;
        _time = time ?? TimeProvider.System;
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
    private ClrProjectLink? CurrentLink => CurrentProfile?.ClrProject;

    /// <summary>The model file of the active connection as currently saved; only without a project (never both).</summary>
    private string? CurrentModelFile => CurrentProfile is { ClrProject: null, ClrModelFile: { } file } ? file : null;

    private ConnectionProfile? CurrentProfile =>
        _active.Profile is { } profile ? _connections.Profiles.FirstOrDefault(p => p.Id == profile.Id) ?? profile : null;

    /// <summary>
    /// Writes the loaded model of the linked project as a model file (WP-64), with its origin (project, Git branch and
    /// commit). Only with a project's model loaded – a model file is passed on as it is.
    /// </summary>
    /// <param name="appVersion">The FerretSharp writing it.</param>
    /// <exception cref="InvalidOperationException">No project's model is loaded.</exception>
    public async Task ExportAsync(string path, string appVersion, CancellationToken cancellationToken)
    {
        if (State is not { Link: { } link, Mapping: { } mapping })
        {
            throw new InvalidOperationException("No project's model loaded.");
        }

        var origin = await Task.Run(() => ModelFile.OriginOf(link, _time.GetLocalNow(), appVersion), cancellationToken);
        await ModelFile.WriteAsync(path, mapping.Model, origin, cancellationToken);
    }

    /// <summary>Reads the model again from the project ("Neu laden"), not from the cache; a load still running is cancelled.</summary>
    public Task LoadAsync() => LoadAsync(useCache: false, buildChanged: false);

    /// <summary>
    /// Lays the loaded model over the schema again (WP-22: after DDL, after "Schema neu laden") – without the model host,
    /// whose export does not depend on the database. Does nothing without a loaded model or while one is loading (that
    /// load maps against the refreshed schema anyway). A load started meanwhile wins.
    /// </summary>
    /// <returns>The new mapping; null if nothing was mapped.</returns>
    public async Task<ClrModelMapping?> RemapAsync(CancellationToken cancellationToken)
    {
        CancellationTokenSource cts;
        ClrModelState state;
        lock (_lock)
        {
            state = State;
            if (state is not { Phase: ClrModelPhase.Loaded, Mapping: { } loaded } || _active.Schema is null || _lifetime.IsCancellationRequested)
            {
                return null;
            }

            _loading?.Cancel();
            _loading = cts = new CancellationTokenSource();
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, cancellationToken);
        var schema = _active.Schema;
        var model = state.Mapping!.Model;
        try
        {
            var mapping = await Task.Run(() => ClrModelMapping.BuildAsync(model, schema, linked.Token), linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            schema.SetForeignKeys(FkSource.ClrModel, mapping.ForeignKeys);
            Set(state with { Mapping = mapping, LoadedAt = _time.GetLocalNow() }, cts);
            return mapping;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return null; // superseded by a load
        }
    }

    /// <param name="useCache">Take the cached model if the build output is unchanged (connecting, link changed, new build).</param>
    /// <param name="buildChanged">Started by the watcher: the status bar says why the model reloads.</param>
    private Task LoadAsync(bool useCache, bool buildChanged)
    {
        CancellationTokenSource cts;
        lock (_lock)
        {
            if (_lifetime.IsCancellationRequested)
            {
                return Task.CompletedTask; // disposed: a late watcher event starts nothing
            }

            _loading?.Cancel();
            _loading = cts = new CancellationTokenSource();
        }

        // Entirely off the caller's thread: mapping a large model (hundreds of entities) must not stall the UI.
        return Task.Run(() => LoadAsync(useCache, buildChanged, cts));
    }

    private async Task LoadAsync(bool useCache, bool buildChanged, CancellationTokenSource cts)
    {
        var link = CurrentLink;
        var modelFile = CurrentModelFile;
        var schema = _active.Schema;
        if (link is null && modelFile is null || schema is null || !_active.IsConnected)
        {
            schema?.SetForeignKeys(FkSource.ClrModel, []); // unlinked: the model's relationships go too
            Watch(null, null, cts);
            Set(ClrModelState.None, cts);
            return;
        }

        var previous = State.Link == link && State.ModelFile == modelFile ? State.Mapping : null;
        var loadedAt = State.LoadedAt;
        var steps = new StepRecorder(_time.GetLocalNow());
        BuildOutput? output = null;
        var origin = modelFile is not null && State.ModelFile == modelFile ? State.Origin : null;

        // Progress: the step (also those the model host reports from its process) with its start, for the model page.
        void Report(string step)
        {
            steps.Start(step);
            Set(new ClrModelState(ClrModelPhase.Loading, link, link is null ? null : output ?? State.Output, previous, null, loadedAt,
                step, _time.GetLocalNow(), steps.StartedAt, BuildChanged: buildChanged, ModelFile: modelFile, Origin: origin), cts);
        }

        DateTimeOffset? cachedAt = null;

        void Finish(ClrModelPhase phase, ClrModelMapping? mapping, ModelHostError? error, DateTimeOffset? at) =>
            Set(new ClrModelState(phase, link, output, mapping, error, at, Steps: steps.Finish(), CachedAt: cachedAt, ModelFile: modelFile,
                Origin: origin), cts);

        try
        {
            if (link is null)
            {
                Report(ClrModelText.StepReadModelFile);
                WatchFile(modelFile!, cts); // also while it is missing: a file copied there later loads
                var read = await ModelFile.ReadAsync(modelFile!, cts.Token);
                if (read is not { Model: { } fromFile, Origin: { } fileOrigin })
                {
                    Finish(ClrModelPhase.Failed, previous, new ModelHostError(ClrModelErrorKind.ModelFile, read.Error ?? ClrModelText.NoModelReturned), loadedAt);
                    return;
                }

                origin = fileOrigin;
                await MapAsync(fromFile);
                return;
            }

            Report(ClrModelText.StepFindBuild);
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
                Report(ClrModelText.StepCheckCache);
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
                // Before the export: a build finishing during it must not get the old model as its own (R4a).
                var fingerprint = _cache?.Fingerprint(link, output);
                var result = await _runner.ReadModelAsync(link, output, cts.Token, new SyncProgress<string>(Report));
                if (result.Model is not { } exported)
                {
                    Finish(ClrModelPhase.Failed, previous, result.Error ?? new ModelHostError(ClrModelErrorKind.HostFailed, ClrModelText.NoModelReturned), loadedAt);
                    return;
                }

                model = exported;
                if (_cache is not null && fingerprint is not null)
                {
                    await _cache.SaveAsync(link, fingerprint, model, cts.Token);
                }
            }

            await MapAsync(model);
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
            Finish(ClrModelPhase.Failed, previous, new ModelHostError(ClrModelErrorKind.HostFailed, TextFormat.Format(ClrModelText.ColumnsNotRead, ex.Display)), loadedAt);
        }
        catch (Exception ex)
        {
            // Runs in the background (nobody awaits it): anything unexpected – a deps.json rewritten by a build running right
            // now, a truncated result file – must end in "Failed", never leave the state at "Loading".
            Finish(ClrModelPhase.Failed, previous, new ModelHostError(ClrModelErrorKind.HostFailed, TextFormat.Format(ClrModelText.ModelNotLoaded, ex.Message)), loadedAt);
        }

        async Task MapAsync(ModelExport model)
        {
            var mapping = await ClrModelMapping.BuildAsync(model, schema, cts.Token, new SyncProgress<string>(Report));
            cts.Token.ThrowIfCancellationRequested();
            schema.SetForeignKeys(FkSource.ClrModel, mapping.ForeignKeys);
            Finish(ClrModelPhase.Loaded, mapping, null, _time.GetLocalNow());
        }
    }

    /// <summary>Durations of the steps of one load: a step lasts until the next one starts.</summary>
    private sealed class StepRecorder(DateTimeOffset startedAt)
    {
        private readonly List<LoadStep> _steps = [];
        private readonly Stopwatch _watch = new();
        private string? _current;

        public DateTimeOffset StartedAt { get; } = startedAt;

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

    /// <summary><c>dotnet build</c> of the linked project, then reads the model again if the build succeeded.</summary>
    public Task<DotNetRun?> BuildAsync() => BuildAsync((link, ct) => _runner.BuildAsync(link, ct), ofProject: true);

    /// <summary>
    /// <c>dotnet build</c> of a solution with the linked project, in its configuration (code usage: projects not built). One
    /// build at a time with <see cref="BuildAsync()"/>; <see cref="LastBuild"/> stays the project's – the result is the
    /// caller's. The model is not read again here: if the build changed the linked project, the build output watcher does,
    /// like after a build in Visual Studio (review of the PR: a forced reload without the cache took up to 12 s more, and
    /// could not be cancelled).
    /// </summary>
    /// <param name="cancellationToken">Stops the build (<c>dotnet build</c> and its processes end); the result is then null.</param>
    public Task<DotNetRun?> BuildSolutionAsync(string solution, CancellationToken cancellationToken) =>
        BuildAsync((link, ct) => _runner.BuildSolutionAsync(solution, link.Configuration, ct), ofProject: false, cancellationToken);

    /// <param name="ofProject">The project's own build: remembered as <see cref="LastBuild"/>, the model read again after it.</param>
    private async Task<DotNetRun?> BuildAsync(Func<ClrProjectLink, CancellationToken, Task<DotNetRun>> build, bool ofProject,
        CancellationToken cancellationToken = default)
    {
        if (CurrentLink is not { } link || IsBuilding)
        {
            return null;
        }

        IsBuilding = true;
        Changed?.Invoke();
        DotNetRun? run = null;
        try
        {
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
            run = await Task.Run(() => build(link, cancel.Token));
        }
        catch (OperationCanceledException)
        {
            return null; // closed or cancelled meanwhile
        }
        catch (Exception ex) when (ex is ClrModelException or IOException or System.ComponentModel.Win32Exception)
        {
            run = new DotNetRun(-1, ex.Message);
        }
        finally
        {
            if (ofProject && run is not null)
            {
                LastBuild = run;
            }

            IsBuilding = false;
            Changed?.Invoke();
        }

        if (ofProject && run.Succeeded)
        {
            await LoadAsync();
        }

        return run;
    }

    public void Dispose()
    {
        _active.Changed -= OnConnectionChanged;
        _connections.Changed -= OnConnectionChanged;
        lock (_lock)
        {
            _lifetime.Cancel();
            // No load owns anything any more (R4a): one still finding the build output would otherwise start a watcher
            // after this – never disposed – and set the state of a closed connection.
            _loading?.Cancel();
            _loading = null;
        }

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
            if (_watcher is { File: null } current && current.Directory == directory && current.DepsOnly == depsOnly)
            {
                return;
            }

            old = _watcher;
            _watcher = null;
            if (directory is not null && Directory.Exists(directory))
            {
                try
                {
                    _watcher = new BuildOutputWatcher(directory, depsOnly, _buildQuiet, () => OnBuildOutputChanged(link!), _time);
                }
                catch (Exception ex) when (ex is IOException or ArgumentException or PlatformNotSupportedException)
                {
                    // no watcher: "Neu laden" and the console's own check still see a new build
                }
            }
        }

        old?.Dispose();
    }

    /// <summary>
    /// Watches a linked model file (WP-64): a new export copied over it reloads the model. Its folder must exist; without
    /// one, "Neu laden" reads the file again.
    /// </summary>
    private void WatchFile(string path, CancellationTokenSource owner)
    {
        var full = Path.GetFullPath(path);
        BuildOutputWatcher? old = null;
        lock (_lock)
        {
            if (!ReferenceEquals(_loading, owner))
            {
                return;
            }

            _loadedFiles = null;
            if (_watcher is { File: not null } current && string.Equals(Path.Combine(current.Directory, current.File), full, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            old = _watcher;
            _watcher = null;
            if (Directory.Exists(Path.GetDirectoryName(full)))
            {
                try
                {
                    _watcher = BuildOutputWatcher.ForFile(full, _buildQuiet, () => OnModelFileChanged(path), _time);
                }
                catch (Exception ex) when (ex is IOException or ArgumentException or PlatformNotSupportedException)
                {
                    // no watcher: "Neu laden" reads the file again
                }
            }
        }

        old?.Dispose();
    }

    /// <summary>The linked model file was replaced or changed: read it again.</summary>
    private void OnModelFileChanged(string path)
    {
        if (_active.IsConnected && CurrentLink is null && CurrentModelFile == path)
        {
            _ = LoadAsync(useCache: false, buildChanged: false);
        }
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
        var modelFile = connected is null ? null : CurrentModelFile;
        if (connected != _profileId || link != State.Link || modelFile != State.ModelFile)
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
