using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Threading;
using FerretSharp.Core.Settings;
using Microsoft.Extensions.Logging;

namespace FerretSharp.App.Services;

/// <summary>
/// Logs when the UI thread does not get to its work for a noticeable time (ADR 0016). The WebView's input and frames pass
/// through the WPF dispatcher, so a stuck UI thread makes typing in the editors stall. A stall is logged with what tells
/// its causes apart: the time spent in dispatcher operations and those that ran long (FerretSharp's work on the UI
/// thread; Blazor handles WebView messages inline, outside of operations), garbage collection pauses and the system's
/// memory load, and the dotnet processes (model host, LINQ console) with their memory. A stall over 1.5 s also gets the
/// stacks of all threads – that is how the process tree kill was found, whose exceptions stopped the whole process under
/// the debugger. Runs only while the setting "Hänger der Oberfläche protokollieren" is on (off by default).
/// </summary>
public sealed class UiStallMonitor : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan Threshold = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan LongOperation = TimeSpan.FromMilliseconds(100);

    /// <summary>WPF's own name of an operation (declaring type and method of its delegate), for the log only.</summary>
    private static readonly PropertyInfo? OperationName = typeof(DispatcherOperation).GetProperty("Name", BindingFlags.Instance | BindingFlags.NonPublic);

    /// <summary>A stall this long gets the stacks of all threads written next to the log (with dotnet-stack, if installed).</summary>
    private static readonly TimeSpan StackAfter = TimeSpan.FromMilliseconds(1500);

    private readonly Dispatcher _dispatcher;
    private readonly ILogger<UiStallMonitor> _logger;
    private readonly string _logDirectory;
    private readonly AppSettingsService _settings;

    /// <summary>The running monitor; null while the setting is off (on the UI thread, like the settings page).</summary>
    private CancellationTokenSource? _run;
    private readonly Lock _lock = new();
    private readonly Dictionary<DispatcherOperation, long> _running = [];
    private readonly List<string> _longOperations = [];
    private int _operations;
    private TimeSpan _operationTime;
    private DateTime _lastStacks = DateTime.MinValue;

    /// <param name="logDirectory">Where the stacks of a long stall are written.</param>
    public UiStallMonitor(Dispatcher dispatcher, ILogger<UiStallMonitor> logger, string logDirectory, AppSettingsService settings)
    {
        _dispatcher = dispatcher;
        _logger = logger;
        _logDirectory = logDirectory;
        _settings = settings;
    }

    /// <summary>Follows the setting from now on: runs while it is on.</summary>
    public void Start()
    {
        _settings.Changed += OnSettingsChanged;
        Apply();
    }

    private void OnSettingsChanged() => _dispatcher.BeginInvoke(Apply);

    private void Apply()
    {
        var on = _settings.Current.DiagnoseUiStalls;
        if (on && _run is null)
        {
            // Hooks fire on the UI thread itself; they only take times.
            _dispatcher.Hooks.OperationStarted += OnOperationStarted;
            _dispatcher.Hooks.OperationCompleted += OnOperationEnded;
            _dispatcher.Hooks.OperationAborted += OnOperationEnded;
            var run = _run = new CancellationTokenSource();
            _ = Task.Run(() => RunAsync(run.Token));
            _logger.LogInformation("UI stall diagnostics on");
        }
        else if (!on && _run is not null)
        {
            Stop();
            _logger.LogInformation("UI stall diagnostics off");
        }
    }

    private void Stop()
    {
        _run?.Cancel();
        _run?.Dispose();
        _run = null;
        _dispatcher.Hooks.OperationStarted -= OnOperationStarted;
        _dispatcher.Hooks.OperationCompleted -= OnOperationEnded;
        _dispatcher.Hooks.OperationAborted -= OnOperationEnded;
        lock (_lock)
        {
            _running.Clear();
        }
    }

    private void OnOperationStarted(object? sender, DispatcherHookEventArgs e)
    {
        lock (_lock)
        {
            _running[e.Operation] = Stopwatch.GetTimestamp();
        }
    }

    private void OnOperationEnded(object? sender, DispatcherHookEventArgs e)
    {
        lock (_lock)
        {
            if (!_running.Remove(e.Operation, out var started))
            {
                return;
            }

            var took = Stopwatch.GetElapsedTime(started);
            _operations++;
            _operationTime += took;
            if (took >= LongOperation && _longOperations.Count < 20)
            {
                var name = OperationName?.GetValue(e.Operation) as string ?? "?";
                _longOperations.Add($"{name} [{e.Operation.Priority}] {(int)took.TotalMilliseconds} ms");
            }
        }
    }

    private async Task RunAsync(CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await Task.Delay(Interval, stop);
                var pauseBefore = GC.GetTotalPauseDuration();
                var gen2Before = GC.CollectionCount(2);
                var gen0Before = GC.CollectionCount(0);
                lock (_lock)
                {
                    _longOperations.Clear();
                    (_operations, _operationTime) = (0, TimeSpan.Zero);
                }

                var watch = Stopwatch.StartNew();
                // Normal, the priority of Blazor's work: WPF holds back Input-priority operations by itself (half a second even
                // on an idle window), which made the probe report stalls that were none.
                var probe = _dispatcher.InvokeAsync(static () => { }, DispatcherPriority.Normal, stop).Task;
                if (await Task.WhenAny(probe, Task.Delay(StackAfter, stop)) != probe)
                {
                    await WriteStacksAsync(); // while the UI thread is still stuck
                }

                await probe;
                if (watch.Elapsed >= Threshold)
                {
                    Report(watch.Elapsed, GC.GetTotalPauseDuration() - pauseBefore, GC.CollectionCount(0) - gen0Before, GC.CollectionCount(2) - gen2Before);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // switched off or shutting down
        }
    }

    private void Report(TimeSpan stall, TimeSpan gcPause, int gen0, int gen2)
    {
        string operations;
        lock (_lock)
        {
            operations = $"{_operations} ops, {(int)_operationTime.TotalMilliseconds} ms in total; long: "
                         + (_longOperations.Count == 0 ? "none" : string.Join("; ", _longOperations));
        }

        var memory = GC.GetGCMemoryInfo();
        var load = memory.TotalAvailableMemoryBytes > 0 ? 100.0 * memory.MemoryLoadBytes / memory.TotalAvailableMemoryBytes : 0;
        _logger.LogWarning(
            "UI thread did not respond for {Milliseconds} ms – dispatcher work: {Operations}; GC pause {GcPause} ms (gen0 {Gen0}, gen2 {Gen2}), "
            + "heap {Heap} MB, working set {WorkingSet} MB, system memory load {Load:0}%; {Hosts}",
            (int)stall.TotalMilliseconds, operations, (int)gcPause.TotalMilliseconds, gen0, gen2, GC.GetTotalMemory(false) / (1024 * 1024),
            Environment.WorkingSet / (1024 * 1024), load, ModelHosts());
    }

    /// <summary>
    /// Runs <c>dotnet-stack report</c> on this process (a .NET global tool: <c>dotnet tool install -g dotnet-stack</c>) and
    /// writes the stacks of all threads to <c>stall-*.txt</c> in the log folder – at most once a minute. Without the tool,
    /// nothing happens.
    /// </summary>
    private async Task WriteStacksAsync()
    {
        var tool = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "tools", "dotnet-stack.exe");
        if (!File.Exists(tool) || DateTime.UtcNow - _lastStacks < TimeSpan.FromMinutes(1))
        {
            return;
        }

        _lastStacks = DateTime.UtcNow;
        var file = Path.Combine(_logDirectory, $"stall-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        try
        {
            using var process = Process.Start(new ProcessStartInfo(tool, new[] { "report", "--process-id", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) })
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            })!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            }
            catch (TimeoutException)
            {
                process.Kill(); // never left behind
                throw;
            }

            await File.WriteAllTextAsync(file, await output + await error);
            _logger.LogWarning("UI thread stuck for more than {Milliseconds} ms – stacks written to {File}", (int)StackAfter.TotalMilliseconds, file);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _logger.LogWarning("dotnet-stack failed: {Error}", ex.Message);
        }
    }

    /// <summary>
    /// The dotnet processes (model host, LINQ console, build servers) with their memory – from the process list, without
    /// opening them (opening a protected process throws, and under a debugger every exception stops the whole process).
    /// </summary>
    private static string ModelHosts()
    {
        var processes = Process.GetProcessesByName("dotnet");
        try
        {
            return processes.Length == 0
                ? "no dotnet processes"
                : "dotnet processes: " + string.Join(", ", processes.Select(p => $"{p.WorkingSet64 / (1024 * 1024)} MB"));
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    public void Dispose()
    {
        _settings.Changed -= OnSettingsChanged;
        Stop();
    }
}
