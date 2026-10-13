using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using FerretSharp.Core.AppTrace;
using FerretSharp.Core.Connections;
using FerretSharp.Core.Resources;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Extensions.Logging;

namespace FerretSharp.AppTrace;

/// <summary>
/// Reads the EF Core events of a running app from outside (WP-33, ADR 0023): an EventPipe session on the app's
/// diagnostics port with the <c>DiagnosticSource</c> bridge, which serializes exactly the fields of
/// <see cref="EfEvents.PayloadSpecs"/>. The app needs no change; the diagnostics port only serves processes of the same
/// user. A session costs the app little (spike: 9,000 queries, no measurable difference) and ends with it.
/// </summary>
public sealed class EventPipeTraceSource(ILogger<EventPipeTraceSource> logger) : IAppTraceSource
{
    private const string Provider = "Microsoft-Diagnostics-DiagnosticSource";
    private const long EventsKeyword = 0x2;
    private const long IgnoreShortCutKeywords = 0x800; // the built-in EF shortcut would add fields of its own

    /// <summary>Buffer of the session in the app; when FerretSharp reads too slowly, the app drops events, it never waits.</summary>
    private const int BufferMegabytes = 64;

    /// <summary>How long ending a trace waits for the app's answer to the stop, and then for the reading to end.</summary>
    internal static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(3);

    public IReadOnlyList<TraceableProcess> ListProcesses()
    {
        var own = Environment.ProcessId;
        var processes = new List<TraceableProcess>();
        foreach (var id in DiagnosticsClient.GetPublishedProcesses().Where(id => id != own))
        {
            try
            {
                using var process = Process.GetProcessById(id);
                var commandLine = ProcessCommandLine.Read(id);
                processes.Add(new TraceableProcess(id, TraceableProcess.NameOf(process.ProcessName, commandLine), commandLine, StartTime(process)));
            }
            catch (ArgumentException)
            {
                // ended meanwhile
            }
            catch (InvalidOperationException)
            {
                // ended meanwhile
            }
        }

        return [.. processes.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Id)];
    }

    public async IAsyncEnumerable<EfEvent> AttachAsync(int processId, [EnumeratorCancellation] CancellationToken ct)
    {
        var session = Start(processId);
        var events = Channel.CreateUnbounded<EfEvent>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        EventPipeEventSource source;
        try
        {
            source = new EventPipeEventSource(session.EventStream); // reads the stream header
        }
        catch (Exception ex)
        {
            DisposeQuietly(session, processId);
            if (HasEnded(processId))
            {
                throw new RefusedException(TextFormat.Format(AppTraceText.ProcessGone, AppName(processId), processId), ex);
            }

            throw;
        }

        source.Dynamic.All += e =>
        {
            if (ToEfEvent(e) is { } efEvent)
            {
                events.Writer.TryWrite(efEvent);
            }
        };

        // TraceEvent reads on its own thread until the stream ends: the app ended, or the session was stopped.
        var reading = Task.Run(() =>
        {
            try
            {
                source.Process();
                events.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                // An app that is killed (stop debugging) cuts the stream mid-block: TraceEvent then fails with "Read past
                // end of stream". That is the app's end, not an error.
                var ended = ct.IsCancellationRequested || HasEnded(processId);
                if (ended)
                {
                    logger.LogDebug(ex, "Trace of process {ProcessId} ended with the app", processId);
                }

                events.Writer.TryComplete(ended ? null : ex);
            }
        }, CancellationToken.None);
        // Stopping is a command to the app's diagnostics server: started once, on the cancellation or in the cleanup below.
        Task? stopping = null;
        var stopLock = new Lock();
        Task StopOnce()
        {
            lock (stopLock)
            {
                return stopping ??= Task.Run(() => Stop(session, processId));
            }
        }

        using var stop = ct.Register(() => StopOnce());
        try
        {
            var reader = events.Reader;
            while (true)
            {
                try
                {
                    if (!await reader.WaitToReadAsync(ct))
                    {
                        break;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }

                while (reader.TryRead(out var e))
                {
                    yield return e;
                }
            }
        }
        finally
        {
            await CleanUpAsync(StopOnce(), reading, source, session, processId);
        }
    }

    /// <summary>
    /// Ends a trace whatever happens: waits a moment for the stop; if it does not come back (an app halted at a breakpoint
    /// does not answer until it runs again), closes the stream itself, so the reading thread ends and no events pile up.
    /// The session is disposed once the stop has come back – in the background, nothing waits for it.
    /// </summary>
    private async Task CleanUpAsync(Task stopping, Task reading, EventPipeEventSource source, EventPipeSession session, int processId)
    {
        if (await Task.WhenAny(stopping, Task.Delay(StopTimeout)) != stopping)
        {
            logger.LogInformation("Stopping the trace of process {ProcessId} takes long (halted?); closing the stream", processId);
            try
            {
                session.EventStream.Dispose();
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Closing the stream of process {ProcessId} failed", processId);
            }
        }

        if (await Task.WhenAny(reading, Task.Delay(StopTimeout)) != reading)
        {
            logger.LogWarning("The trace of process {ProcessId} did not end its reading", processId);
        }

        try
        {
            source.Dispose();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Disposing the event source of process {ProcessId} failed", processId);
        }

        _ = stopping.ContinueWith(_ => DisposeQuietly(session, processId), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    private void DisposeQuietly(EventPipeSession session, int processId)
    {
        try
        {
            session.Dispose();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Disposing the session of process {ProcessId} failed", processId);
        }
    }

    private EventPipeSession Start(int processId)
    {
        var name = AppName(processId);
        var providers = new[]
        {
            new EventPipeProvider(Provider, EventLevel.Verbose, EventsKeyword | IgnoreShortCutKeywords,
                new Dictionary<string, string> { ["FilterAndPayloadSpecs"] = EfEvents.PayloadSpecs }),
        };
        try
        {
            return new DiagnosticsClient(processId).StartEventPipeSession(providers, requestRundown: false, circularBufferMB: BufferMegabytes);
        }
        catch (ServerNotAvailableException ex) when (!IsRunning(processId))
        {
            throw new RefusedException(TextFormat.Format(AppTraceText.ProcessGone, name, processId), ex);
        }
        catch (Exception ex) when (ex is ServerNotAvailableException or ServerErrorException or UnauthorizedAccessException or UnsupportedCommandException)
        {
            logger.LogWarning(ex, "Attaching to process {ProcessId} failed", processId);
            throw new RefusedException(TextFormat.Format(AppTraceText.NoDiagnostics, name, processId, ex.Message), ex);
        }
    }

    /// <summary>Ends the session; the app may have ended already, then there is nothing to stop. Never throws.</summary>
    private void Stop(EventPipeSession session, int processId)
    {
        try
        {
            session.Stop();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Stopping the trace of process {ProcessId} failed (ended or not answering)", processId);
        }
    }

    /// <summary>
    /// An event of the <c>DiagnosticSource</c> bridge: its name and the requested fields, as text. The bridge also sends
    /// its own messages (another event name), which are skipped.
    /// </summary>
    internal static EfEvent? ToEfEvent(TraceEvent e)
    {
        if (e.ProviderName != Provider || e.EventName != "Event" || e.PayloadByName("EventName") is not string name)
        {
            return null;
        }

        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        if (e.PayloadByName("Arguments") is IEnumerable<object> arguments)
        {
            foreach (var argument in arguments.OfType<IDictionary<string, object>>())
            {
                if (argument.TryGetValue("Key", out var key) && key is string k && argument.TryGetValue("Value", out var value))
                {
                    fields[k] = value as string ?? "";
                }
            }
        }

        return new EfEvent(name, fields, new DateTimeOffset(e.TimeStamp));
    }

    private static string AppName(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return TraceableProcess.NameOf(process.ProcessName, ProcessCommandLine.Read(processId));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return "?";
        }
    }

    /// <summary>
    /// The process has ended – waiting a moment for it: a killed app closes its end of the pipe slightly before the process
    /// counts as exited (review of WP-33).
    /// </summary>
    private static bool HasEnded(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.WaitForExit(500);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return true;
        }
    }

    private static bool IsRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static DateTimeOffset? StartTime(Process process)
    {
        try
        {
            return process.StartTime;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return null;
        }
    }
}
