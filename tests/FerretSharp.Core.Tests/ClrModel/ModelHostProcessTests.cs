using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Resources;

namespace FerretSharp.Core.Tests.ClrModel;

/// <summary>
/// The process path of the model host (open since R3b, done in R4b) with a stand-in (tests/FerretSharp.FakeModelHost) that
/// plays each way the host can behave: ready, never connecting, exiting or failing at start, crashing or answering garbage
/// in a request, not answering a run, ending an export without a result.
/// </summary>
public sealed class ModelHostProcessTests
{
    private static readonly string Configuration = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
        StringComparison.OrdinalIgnoreCase) ? "Release" : "Debug";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string FakeHost
    {
        get
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (!File.Exists(Path.Combine(root.FullName, "FerretSharp.slnx")))
            {
                root = root.Parent ?? throw new InvalidOperationException("FerretSharp.slnx not found");
            }

            return Path.Combine(root.FullName, "tests", "FerretSharp.FakeModelHost", "bin", Configuration, "net10.0", "FerretSharp.FakeModelHost.dll");
        }
    }

    /// <summary>The fake's own build: its deps.json and runtime, as the runner would find them for a linked project.</summary>
    private static BuildOutput Output => new(FakeHost, Path.ChangeExtension(FakeHost, ".deps.json"), new Version(Environment.Version.Major, Environment.Version.Minor),
        DateTime.UtcNow, [], [], null);

    private sealed class Steps : IProgress<string>
    {
        private readonly List<string> _steps = [];

        public IReadOnlyList<string> All
        {
            get
            {
                lock (_steps)
                {
                    return _steps.ToList();
                }
            }
        }

        public void Report(string value)
        {
            lock (_steps)
            {
                _steps.Add(value);
            }
        }
    }

    private static Task<LinqConsoleHost> StartAsync(string scenario, TimeSpan? start = null, TimeSpan? run = null, IProgress<string>? progress = null) =>
        StartAsync(scenario, Directory.CreateDirectory(Path.Combine(ModelHostRunner.WorkRoot, Guid.NewGuid().ToString("N"))), start, run, progress);

    private static Task<LinqConsoleHost> StartAsync(string scenario, DirectoryInfo work, TimeSpan? start = null, TimeSpan? run = null, IProgress<string>? progress = null)
    {
        var pipe = "ferretsharp-test-" + Guid.NewGuid().ToString("N");
        return LinqConsoleHost.StartAsync(["exec", FakeHost, "--console", pipe, "--scenario", scenario], Path.GetDirectoryName(FakeHost)!, pipe, work, null, Output,
            start ?? TimeSpan.FromSeconds(30), run ?? TimeSpan.FromSeconds(30), progress, Ct);
    }

    [Fact]
    public async Task A_ready_host_reports_its_steps_runs_and_ends_with_its_work_folder()
    {
        var steps = new Steps();
        var work = Directory.CreateDirectory(Path.Combine(ModelHostRunner.WorkRoot, Guid.NewGuid().ToString("N")));
        var host = await StartAsync("ready", work, progress: steps);

        var result = await host.RunAsync("return 1;", "", Ct);

        Assert.True(host.IsAlive);
        Assert.Equal("SELECT 1 FROM DUAL", Assert.Single(result.Commands).Sql);
        Assert.Contains("Fake host starting", steps.All);

        await host.DisposeAsync();
        Assert.False(host.IsAlive);
        work.Refresh();
        Assert.False(work.Exists);
    }

    [Fact]
    public async Task A_host_that_never_connects_is_stopped_after_the_start_timeout()
    {
        var failure = await Assert.ThrowsAsync<ClrModelException>(() => StartAsync("never-connects", start: TimeSpan.FromSeconds(2)));

        Assert.Equal(ClrModelErrorKind.Timeout, failure.Kind);
        Assert.Equal(TextFormat.Format(ClrModelText.HelperStartTimeout, 2.0), failure.Message);
    }

    /// <summary>
    /// The exit code and what the host wrote to stderr – reported once the start timeout is over: the wait for the pipe does
    /// not end when the process does (backlog: notice the exit right away).
    /// </summary>
    [Fact]
    public async Task A_host_that_exits_at_start_says_so_with_its_error_output()
    {
        var failure = await Assert.ThrowsAsync<ClrModelException>(() => StartAsync("exit-at-start", start: TimeSpan.FromSeconds(3)));

        Assert.Equal(TextFormat.Format(ClrModelText.HelperExitedAtStart, 3), failure.Message);
        Assert.Contains("The fake host gave up.", failure.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_host_that_cannot_load_the_project_reports_its_error()
    {
        var failure = await Assert.ThrowsAsync<ClrModelException>(() => StartAsync("start-error"));

        Assert.Equal((ModelHostErrorKind.NoContext, "No DbContext in the fake project.", "fake detail"), (failure.Kind, failure.Message, failure.Detail));
    }

    [Fact]
    public async Task A_crash_in_a_request_ends_the_console_and_later_requests_say_it_is_gone()
    {
        await using var host = await StartAsync("crash-in-run");

        var crash = await Assert.ThrowsAsync<ClrModelException>(() => host.RunAsync("return 1;", "", Ct));

        Assert.Equal(ClrModelText.ConsoleCrashed, crash.Message);
        Assert.Contains("The fake host crashed.", crash.Detail, StringComparison.Ordinal);
        Assert.False(host.IsAlive);
        var gone = await Assert.ThrowsAsync<ClrModelException>(() => host.CompleteAsync("", "", LinqProtocol.CodeSection, 0, Ct));
        Assert.Equal((ClrModelErrorKind.HostFailed, ClrModelText.ConsoleNotRunning), (gone.Kind, gone.Message));
    }

    [Fact]
    public async Task An_unreadable_answer_stops_the_console()
    {
        await using var host = await StartAsync("unreadable");

        var failure = await Assert.ThrowsAsync<ClrModelException>(() => host.RunAsync("return 1;", "", Ct));

        Assert.Equal(ClrModelText.ConsoleUnreadable, failure.Message);
        Assert.False(host.IsAlive);
    }

    [Fact]
    public async Task A_run_that_does_not_answer_is_given_up_after_the_run_timeout()
    {
        await using var host = await StartAsync("hangs-in-run", run: TimeSpan.FromSeconds(1));

        var failure = await Assert.ThrowsAsync<ClrModelException>(() => host.RunAsync("while (true) { }", "", Ct));

        Assert.Equal((ClrModelErrorKind.Timeout, TextFormat.Format(ClrModelText.RunTimeout, 1.0)), (failure.Kind, failure.Message));
        Assert.False(host.IsAlive);
    }

    [Fact]
    public async Task Cancelling_a_run_stops_the_console_without_an_error()
    {
        await using var host = await StartAsync("hangs-in-run");
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cancel.CancelAfter(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.RunAsync("while (true) { }", "", cancel.Token));

        Assert.False(host.IsAlive); // its answer would no longer match the next request
    }

    // ---- the model export ----

    private static Task<ModelHostResult> ExportAsync(string scenario, string? host = null) =>
        new ModelHostRunner(host ?? FakeHost, TimeSpan.FromSeconds(30)).ReadModelAsync(new ClrProjectLink("Fake.csproj", Configuration, scenario), Output, Ct);

    [Fact]
    public async Task An_export_without_a_result_file_says_how_the_host_ended()
    {
        var result = await ExportAsync("no-result");

        Assert.Null(result.Model);
        Assert.Equal((ClrModelErrorKind.HostFailed, TextFormat.Format(ClrModelText.HostEndedWithoutResult, 4)), (result.Error?.Kind, result.Error?.Message));
        Assert.Contains("The fake host ended without a model.", result.Error?.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_truncated_result_file_is_an_error_like_the_others()
    {
        var result = await ExportAsync("truncated");

        Assert.Equal(ClrModelErrorKind.HostFailed, result.Error?.Kind);
        Assert.StartsWith(TextFormat.Format(ClrModelText.HostNotRunnable, ""), result.Error?.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_host_s_own_error_comes_back_and_a_missing_host_is_named()
    {
        Assert.Equal(ModelHostErrorKind.NoContext, (await ExportAsync("error")).Error?.Kind);

        var missing = Path.Combine(Path.GetDirectoryName(FakeHost)!, "Missing.dll");
        Assert.Equal(TextFormat.Format(ClrModelText.HostMissing, missing), (await ExportAsync("error", missing)).Error?.Message);
    }
}
