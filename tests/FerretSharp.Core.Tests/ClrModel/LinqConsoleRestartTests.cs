using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Resources;
using NSubstitute;

namespace FerretSharp.Core.Tests.ClrModel;

/// <summary>
/// R4b: restarting the LINQ console for a new build, out of <see cref="LinqConsoleService"/> – when it starts, one at a
/// time, what it reports and when it gives up (the cases through the service stay in <c>ClrModelLoadingTests</c>).
/// </summary>
public sealed class LinqConsoleRestartTests
{
    private static readonly ClrProjectLink Link = new("Shop.csproj");

    private static readonly IReadOnlyDictionary<string, FileStamp> Files = new Dictionary<string, FileStamp>();

    private static ClrModelState State(ClrModelPhase phase) => new(phase, Link, null, null, null, null);

    private static ILinqConsole Console()
    {
        var console = Substitute.For<ILinqConsole>();
        console.IsAlive.Returns(true);
        return console;
    }

    private static bool Disposed(ILinqConsole console) => console.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(ILinqConsole.DisposeAsync));

    /// <summary>A console the restart works for: hosts start when the test says so, every state set is recorded.</summary>
    private sealed class Target : LinqConsoleRestart.ITarget
    {
        private readonly Lock _lock = new();
        private readonly List<LinqConsoleState> _states = [];
        private readonly List<(TaskCompletionSource<ILinqConsole> Start, CancellationToken Token)> _starts = [];

        public ClrProjectLink? RunningLink { get; set; } = Link;

        public ClrProjectLink? ModelLink { get; set; } = Link;

        /// <summary>False: the console no longer wants the new host (it ended, another link).</summary>
        public bool Takes { get; set; } = true;

        public List<ILinqConsole> Taken { get; } = [];

        public ILinqConsole Old { get; } = Console();

        public int Changes { get; private set; }

        public IReadOnlyList<LinqConsoleState> States
        {
            get
            {
                lock (_lock)
                {
                    return _states.ToList();
                }
            }
        }

        public (TaskCompletionSource<ILinqConsole> Start, CancellationToken Token) Start(int index)
        {
            lock (_lock)
            {
                return _starts[index];
            }
        }

        public int Started
        {
            get
            {
                lock (_lock)
                {
                    return _starts.Count;
                }
            }
        }

        public async Task<(ILinqConsole Console, IReadOnlyDictionary<string, FileStamp> Files)> StartHostAsync(ClrProjectLink link, Action<string> report, CancellationToken cancellationToken)
        {
            var start = new TaskCompletionSource<ILinqConsole>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_lock)
            {
                _starts.Add((start, cancellationToken));
            }

            report("Modell bauen");
            return (await start.Task.WaitAsync(cancellationToken), Files);
        }

        public Task<(bool Taken, ILinqConsole? Replaced)> SwapAsync(ClrProjectLink link, ILinqConsole started, IReadOnlyDictionary<string, FileStamp> files, CancellationToken cancellationToken)
        {
            if (!Takes)
            {
                return Task.FromResult<(bool, ILinqConsole?)>((false, null));
            }

            Taken.Add(started);
            return Task.FromResult<(bool, ILinqConsole?)>((true, Old));
        }

        public IReadOnlyDictionary<string, FileStamp>? ListFiles(ClrProjectLink link) => Files;

        public void SetState(LinqConsoleState state)
        {
            lock (_lock)
            {
                _states.Add(state);
            }
        }

        public void RaiseChanged() => Changes++;

        public async Task WaitForStartsAsync(int count)
        {
            for (var i = 0; i < 300 && Started < count; i++)
            {
                await Task.Delay(10, TestContext.Current.CancellationToken);
            }

            Assert.Equal(count, Started);
        }
    }

    [Fact]
    public async Task A_build_waits_for_the_model_reload_then_one_restart_replaces_the_host()
    {
        var target = new Target();
        var restart = new LinqConsoleRestart(target);
        var before = State(ClrModelPhase.Loaded);

        restart.BuildChanged(before);
        restart.ModelChanged(before, sameLink: true); // the same state: the reload has not begun
        restart.ModelChanged(State(ClrModelPhase.Loading), sameLink: true);
        Assert.True(restart.IsWaiting);
        Assert.Equal(0, target.Started);

        restart.ModelChanged(State(ClrModelPhase.Loaded), sameLink: true);
        Assert.False(restart.IsWaiting);
        await target.WaitForStartsAsync(1);
        var fresh = Console();
        target.Start(0).Start.SetResult(fresh);
        await restart.Running;

        Assert.Equal([fresh], target.Taken);
        Assert.True(Disposed(target.Old));
        Assert.False(Disposed(fresh));
        Assert.Equal([LinqConsoleService.ReloadStep(ClrModelText.ReloadStarting), LinqConsoleService.ReloadStep("Modell bauen"), null],
            target.States.Select(s => s.Step));
        Assert.All(target.States, s => Assert.Equal(LinqConsolePhase.Ready, s.Phase));
        Assert.Equal(3, target.Changes);
    }

    [Fact]
    public async Task Another_link_after_the_reload_restarts_nothing()
    {
        var target = new Target();
        var restart = new LinqConsoleRestart(target);
        restart.BuildChanged(State(ClrModelPhase.Loaded));

        restart.ModelChanged(State(ClrModelPhase.Loaded), sameLink: false);

        await restart.Running;
        Assert.Equal(0, target.Started);
        Assert.False(restart.IsWaiting);
    }

    /// <summary>R4b: a restart superseded while its host started never overwrites the state of the one after it.</summary>
    [Fact]
    public async Task A_newer_build_cancels_the_restart_running_its_host_goes_and_it_reports_nothing_more()
    {
        var target = new Target();
        var restart = new LinqConsoleRestart(target);
        restart.StartIfIdle();
        await target.WaitForStartsAsync(1);
        var first = target.Start(0);

        restart.BuildChanged(State(ClrModelPhase.Loaded)); // a newer build
        Assert.True(first.Token.IsCancellationRequested);
        var late = Console();
        first.Start.TrySetResult(late);
        var states = target.States.Count;
        restart.ModelChanged(State(ClrModelPhase.Loaded), sameLink: true);
        await target.WaitForStartsAsync(2);
        var fresh = Console();
        target.Start(1).Start.SetResult(fresh);
        await restart.Running;

        Assert.Equal([fresh], target.Taken); // the late host of the first restart was never taken
        Assert.Equal([LinqConsoleService.ReloadStep(ClrModelText.ReloadStarting), LinqConsoleService.ReloadStep("Modell bauen"), null],
            target.States.Skip(states).Select(s => s.Step));
    }

    [Fact]
    public async Task One_restart_at_a_time_unless_a_newer_build_supersedes_it()
    {
        var target = new Target();
        var restart = new LinqConsoleRestart(target);
        restart.StartIfIdle();
        await target.WaitForStartsAsync(1);

        restart.StartIfIdle(); // the watcher missed a build again: the running restart takes the newest output anyway
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Equal(1, target.Started);
        Assert.False(target.Start(0).Token.IsCancellationRequested);
        target.Start(0).Start.SetResult(Console());
        await restart.Running;
    }

    [Fact]
    public async Task A_build_that_cannot_be_loaded_keeps_the_old_host_says_why_and_is_not_tried_again()
    {
        var target = new Target();
        var restart = new LinqConsoleRestart(target);
        restart.StartIfIdle();
        await target.WaitForStartsAsync(1);

        target.Start(0).Start.SetException(new ClrModelException(ClrModelErrorKind.HostFailed, "OnModelCreating kaputt", "Stack"));
        await restart.Running;

        Assert.Empty(target.Taken);
        Assert.False(Disposed(target.Old));
        var state = target.States[^1];
        Assert.Equal((LinqConsolePhase.Ready, ClrModelErrorKind.HostFailed, "Stack"), (state.Phase, state.Error?.Kind, state.Error?.Detail));
        Assert.Equal(TextFormat.Format(ClrModelText.NewBuildNotLoaded, "OnModelCreating kaputt"), state.Error?.Message);
        Assert.True(restart.FailedFor(Files));
    }

    [Fact]
    public async Task A_host_the_console_no_longer_wants_is_disposed()
    {
        var target = new Target { Takes = false };
        var restart = new LinqConsoleRestart(target);
        restart.StartIfIdle();
        await target.WaitForStartsAsync(1);

        var unwanted = Console();
        target.Start(0).Start.SetResult(unwanted);
        await restart.Running;

        Assert.True(Disposed(unwanted));
        Assert.False(Disposed(target.Old));
        Assert.DoesNotContain(target.States, s => s.Step is null); // no "ready" for a host that was not taken
    }

    [Fact]
    public async Task After_the_end_no_restart_starts_and_one_running_is_cancelled()
    {
        var target = new Target();
        var restart = new LinqConsoleRestart(target);
        restart.StartIfIdle();
        await target.WaitForStartsAsync(1);

        var running = restart.End();
        await running;

        Assert.True(target.Start(0).Token.IsCancellationRequested);
        restart.StartIfIdle();
        restart.BuildChanged(State(ClrModelPhase.Loaded));
        restart.ModelChanged(State(ClrModelPhase.Loaded), sameLink: true);
        await restart.Running;
        Assert.Equal(1, target.Started);
    }

    [Fact]
    public async Task Without_a_running_console_there_is_nothing_to_restart()
    {
        var target = new Target { RunningLink = null };
        var restart = new LinqConsoleRestart(target);

        restart.StartIfIdle();
        await restart.Running;

        Assert.Equal(0, target.Started);
        Assert.Empty(target.States);
    }
}
