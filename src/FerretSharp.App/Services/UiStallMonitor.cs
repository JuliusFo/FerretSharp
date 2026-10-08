using System.Diagnostics;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;

namespace FerretSharp.App.Services;

/// <summary>
/// Logs when the UI thread does not get to input for a noticeable time. The WebView's input and frames pass through the
/// WPF dispatcher, so a busy dispatcher makes typing in the editors stall; the log says when and for how long (to tell
/// that apart from a busy CPU, which this does not report).
/// </summary>
public sealed class UiStallMonitor(Dispatcher dispatcher, ILogger<UiStallMonitor> logger) : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan Threshold = TimeSpan.FromMilliseconds(300);

    private readonly CancellationTokenSource _stop = new();

    public void Start() => _ = Task.Run(RunAsync);

    private async Task RunAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await Task.Delay(Interval, _stop.Token);
                var watch = Stopwatch.StartNew();
                await dispatcher.InvokeAsync(static () => { }, DispatcherPriority.Input, _stop.Token);
                if (watch.Elapsed >= Threshold)
                {
                    logger.LogWarning("UI thread did not get to input for {Milliseconds} ms", (int)watch.Elapsed.TotalMilliseconds);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }
}
