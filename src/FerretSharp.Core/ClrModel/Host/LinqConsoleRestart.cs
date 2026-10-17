using FerretSharp.Core.Resources;

namespace FerretSharp.Core.ClrModel;

/// <summary>
/// Restarting the LINQ console for a new build (ADR 0016; out of <see cref="LinqConsoleService"/> since R4b, with tests): a
/// host on the new build starts next to the running one and replaces it once ready – runs meanwhile go to the old one; if
/// the new build cannot be loaded, the old host stays and the state says why. A restart waits for the model reload the
/// build started (two hosts building a large model at once made the UI stutter), runs one at a time – a newer build
/// cancels the one running –, is not tried again for a build output that failed to load, and starts no more once the
/// console ended. The state it reports is set under its lock, together with the check that it was not superseded, so a
/// cancelled restart never overwrites what came after it.
/// </summary>
internal sealed class LinqConsoleRestart(LinqConsoleRestart.ITarget target)
{
    /// <summary>The console a restart works for.</summary>
    internal interface ITarget
    {
        /// <summary>The link of the host that runs; null if none runs (the next start reads the new build anyway).</summary>
        ClrProjectLink? RunningLink { get; }

        /// <summary>The project the model manager has linked now.</summary>
        ClrProjectLink? ModelLink { get; }

        /// <summary>Finds the build and starts a host on it; <paramref name="report"/> tells its steps.</summary>
        Task<(ILinqConsole Console, IReadOnlyDictionary<string, FileStamp> Files)> StartHostAsync(ClrProjectLink link, Action<string> report, CancellationToken cancellationToken);

        /// <summary>
        /// Takes the host started for <paramref name="link"/> if the console still wants it (same link, not ended) and gives
        /// back the one it replaces; false: the started host is not taken (and disposed by the restart).
        /// </summary>
        Task<(bool Taken, ILinqConsole? Replaced)> SwapAsync(ClrProjectLink link, ILinqConsole started, IReadOnlyDictionary<string, FileStamp> files, CancellationToken cancellationToken);

        /// <summary>The files of the link's build output; null if they cannot be listed.</summary>
        IReadOnlyDictionary<string, FileStamp>? ListFiles(ClrProjectLink link);

        /// <summary>Sets the console's state – without telling anybody yet (the restart holds its lock).</summary>
        void SetState(LinqConsoleState state);

        /// <summary>Tells the listeners the state changed.</summary>
        void RaiseChanged();
    }

    private readonly Lock _lock = new();

    /// <summary>A build changed the output while the model was (about to be) reloaded: restart once the state moved on from this one.</summary>
    private ClrModelState? _after;

    private CancellationTokenSource? _running;
    private Task _task = Task.CompletedTask;
    private bool _ended;

    /// <summary>The build output a restart failed for: not tried again until the output changes.</summary>
    private IReadOnlyDictionary<string, FileStamp>? _failedFiles;

    /// <summary>The restart running, or the last one (tests wait for it).</summary>
    internal Task Running
    {
        get
        {
            lock (_lock)
            {
                return _task;
            }
        }
    }

    /// <summary>A restart waits for the model reload of a build (tests).</summary>
    internal bool IsWaiting
    {
        get
        {
            lock (_lock)
            {
                return _after is not null;
            }
        }
    }

    /// <summary>A build changed the output: a restart on the previous build is outdated; the next waits for the model reload.</summary>
    public void BuildChanged(ClrModelState state)
    {
        lock (_lock)
        {
            _running?.Cancel();
            _after = state;
        }
    }

    /// <summary>
    /// The model manager reported a step: once the reload after the build has ended (another state, not loading), the
    /// waiting restart starts – if the console still belongs to that project (<paramref name="sameLink"/>).
    /// </summary>
    public void ModelChanged(ClrModelState state, bool sameLink)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (_lock)
        {
            if (_after is not { } after || ReferenceEquals(after, state) || state.Phase == ClrModelPhase.Loading)
            {
                return;
            }

            _after = null;
            if (sameLink)
            {
                StartLocked(supersede: true);
            }
        }
    }

    /// <summary>Starts a restart now (the watcher missed a build) – unless one runs already.</summary>
    public void StartIfIdle()
    {
        lock (_lock)
        {
            StartLocked(supersede: false);
        }
    }

    /// <summary>A restart running is outdated (a foreground start reads the newest build; the host stops; another link).</summary>
    /// <param name="dropWaiting">Also forget a restart waiting for the model reload.</param>
    public void Cancel(bool dropWaiting)
    {
        lock (_lock)
        {
            _running?.Cancel();
            if (dropWaiting)
            {
                _after = null;
            }
        }
    }

    /// <summary>A restart already failed for exactly these files.</summary>
    public bool FailedFor(IReadOnlyDictionary<string, FileStamp> files)
    {
        lock (_lock)
        {
            return _failedFiles is { } failed && BuildFiles.Same(failed, files);
        }
    }

    /// <summary>The console ends: no restart starts or swaps hosts after this. Returns the restart running, to wait for.</summary>
    public Task End()
    {
        lock (_lock)
        {
            _ended = true;
            _running?.Cancel();
            return _task;
        }
    }

    private void StartLocked(bool supersede)
    {
        if (_ended || (!supersede && !_task.IsCompleted))
        {
            return;
        }

        _running?.Cancel();
        var cts = _running = new CancellationTokenSource();
        _task = Task.Run(() => RunAsync(cts));
    }

    private async Task RunAsync(CancellationTokenSource cts)
    {
        if (target.RunningLink is not { } link || target.ModelLink != link)
        {
            return; // not running: the next start reads the new build
        }

        void Reloading(string step) => Report(cts, new LinqConsoleState(LinqConsolePhase.Ready, LinqConsoleService.ReloadStep(step)));

        Reloading(ClrModelText.ReloadStarting);
        ILinqConsole? started = null;
        IReadOnlyDictionary<string, FileStamp>? files = null;
        try
        {
            (started, files) = await target.StartHostAsync(link, Reloading, cts.Token);
            var (taken, replaced) = await target.SwapAsync(link, started, files, cts.Token);
            if (!taken)
            {
                return; // the console ended or belongs to another link meanwhile: the new host goes too
            }

            started = null;
            lock (_lock)
            {
                _failedFiles = null;
            }

            Report(cts, new LinqConsoleState(LinqConsolePhase.Ready));
            if (replaced is not null)
            {
                await replaced.DisposeAsync();
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // superseded by a newer build, a foreground start, another link or the end
        }
        catch (Exception ex) when (ex is ClrModelException || LinqConsoleService.IsStartFailure(ex))
        {
            var error = LinqConsoleService.StartError(ex);
            var failed = files ?? target.ListFiles(link);
            lock (_lock)
            {
                _failedFiles = failed;
            }

            Report(cts, new LinqConsoleState(LinqConsolePhase.Ready, Error: new ModelHostError(error.Kind,
                TextFormat.Format(ClrModelText.NewBuildNotLoaded, error.Message), error.Detail)));
        }
        finally
        {
            if (started is not null)
            {
                await started.DisposeAsync();
            }
        }
    }

    /// <summary>The restart's state – unless it was superseded meanwhile: checked and set under the lock that cancels it.</summary>
    private void Report(CancellationTokenSource cts, LinqConsoleState state)
    {
        lock (_lock)
        {
            if (cts.IsCancellationRequested)
            {
                return;
            }

            target.SetState(state);
        }

        target.RaiseChanged();
    }
}
