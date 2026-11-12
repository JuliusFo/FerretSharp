using System.Diagnostics;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;

namespace FerretSharp.App.Services;

/// <summary>
/// Notes in the log when the UI thread does not respond for seconds – always on, unlike the detailed
/// <see cref="UiStallMonitor"/> (a setting, off by default). A hang of three minutes (2026-11-02, over RDP, "Not
/// responding" in the title) left only a debug line of the WebView's capture behind; now its start is logged while it
/// lasts (should the app be ended in it) and its end with the duration, next to the display and session changes of
/// <see cref="DisplayWatch"/>. Across sleep a wait may be noted that was none: "Power mode Resume" stands next to it.
/// </summary>
public sealed class UiHangWatch(Dispatcher dispatcher, ILogger<UiHangWatch> logger) : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Threshold = TimeSpan.FromSeconds(5);

    private readonly CancellationTokenSource _stop = new();

    public void Start() => _ = Task.Run(() => RunAsync(_stop.Token));

    private async Task RunAsync(CancellationToken stop)
    {
        try
        {
            while (true)
            {
                await Task.Delay(Interval, stop);
                var since = DateTime.Now;
                var started = Stopwatch.GetTimestamp();
                // Normal, the priority of Blazor's work, as in UiStallMonitor (Input is held back by WPF itself).
                var probe = dispatcher.InvokeAsync(static () => { }, DispatcherPriority.Normal, stop).Task;
                try
                {
                    await probe.WaitAsync(Threshold, stop);
                    continue;
                }
                catch (TimeoutException)
                {
                    logger.LogWarning("UI thread not responding for {Seconds} s (since {Since:HH:mm:ss.fff})", Threshold.TotalSeconds, since);
                }

                await probe;
                logger.LogWarning("UI thread responding again after {Seconds:0.0} s (since {Since:HH:mm:ss.fff})",
                    Stopwatch.GetElapsedTime(started).TotalSeconds, since);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    private int _disposed;

    /// <summary>Stops the watch; also before the host disposes it (<c>App.OnExit</c>), so a second call does nothing.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _stop.Cancel();
            _stop.Dispose();
        }
    }
}
