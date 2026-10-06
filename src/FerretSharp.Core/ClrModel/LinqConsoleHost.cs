using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace FerretSharp.Core.ClrModel;

/// <summary>The LINQ console of a linked project: translates C# into the SQL EF would send (ADR 0011).</summary>
public interface ILinqConsole : IAsyncDisposable
{
    /// <summary>The build the host loaded (a newer build needs a new console).</summary>
    BuildOutput Output { get; }

    /// <summary>False once the host has exited or stopped answering.</summary>
    bool IsAlive { get; }

    /// <summary>Compiles and runs the code in the host; nothing reaches the database.</summary>
    /// <exception cref="ClrModelException">The host did not answer in time or has died (it is then stopped).</exception>
    Task<LinqRunResult> RunAsync(string code, string variables, CancellationToken cancellationToken);

    /// <summary>Completion items at the cursor (WP-19): members after a dot, otherwise variables, types and keywords.</summary>
    /// <param name="section"><see cref="LinqProtocol.CodeSection"/> or <see cref="LinqProtocol.VariablesSection"/>: where the cursor is.</param>
    /// <param name="offset">The cursor in that section's text.</param>
    /// <exception cref="ClrModelException">The host did not answer in time or has died (it is then stopped).</exception>
    Task<IReadOnlyList<LinqCompletionItem>> CompleteAsync(string code, string variables, string section, int offset, CancellationToken cancellationToken);
}

/// <summary>
/// FerretSharp.ModelHost running as LINQ console: started once per linked project, it keeps the project and its model
/// loaded. Requests go one at a time as JSON lines over a named pipe; the host's stdout only carries progress lines.
/// </summary>
public sealed class LinqConsoleHost : ILinqConsole
{
    /// <summary>The host gives up on a run after 30 s itself; this covers code that never returns.</summary>
    public static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(45);

    private readonly Process _process;
    private readonly NamedPipeServerStream _pipe;
    private StreamReader _reader = StreamReader.Null;
    private StreamWriter _writer = StreamWriter.Null;
    private readonly DirectoryInfo _work;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly StringBuilder _errors;
    private int _nextId;
    private bool _dead;

    private LinqConsoleHost(Process process, NamedPipeServerStream pipe, DirectoryInfo work, BuildOutput output, StringBuilder errors)
    {
        _process = process;
        _pipe = pipe;
        _work = work;
        _errors = errors;
        Output = output;
    }

    public BuildOutput Output { get; }

    public bool IsAlive => !_dead && !_process.HasExited;

    /// <param name="arguments">The <c>dotnet exec</c> arguments with <c>--console &lt;pipe&gt;</c>.</param>
    /// <param name="work">Temporary folder (runtimeconfig); deleted when the console ends.</param>
    /// <param name="startTimeout">Loading the project and building its model must finish within this.</param>
    internal static async Task<LinqConsoleHost> StartAsync(
        IReadOnlyList<string> arguments, string workingDirectory, string pipeName, DirectoryInfo work, BuildOutput output,
        TimeSpan startTimeout, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var errors = new StringBuilder();
        Process? process = null;
        try
        {
            progress?.Report("Starte den Hilfsprozess");
            process = DotNetCli.Start(arguments, workingDirectory, line =>
            {
                if (line.StartsWith(ModelHostResult.ProgressPrefix, StringComparison.Ordinal))
                {
                    progress?.Report(line[ModelHostResult.ProgressPrefix.Length..]);
                }
            }, line =>
            {
                lock (errors)
                {
                    if (errors.Length < 20_000)
                    {
                        errors.AppendLine(line);
                    }
                }
            });

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(startTimeout);
            var host = new LinqConsoleHost(process, pipe, work, output, errors);
            try
            {
                await pipe.WaitForConnectionAsync(timeout.Token).WaitAsync(timeout.Token);
                // Only now: with AutoFlush, a writer flushes at once, and an unconnected pipe refuses that.
                host._reader = new StreamReader(pipe, new UTF8Encoding(false));
                host._writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
                var ready = await host.ReadResponseAsync(0, timeout.Token);
                if (ready.Error is { } error)
                {
                    throw new ClrModelException(error.Kind, error.Message, error.Detail);
                }

                return host;
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException)
            {
                await host.DisposeAsync();
                cancellationToken.ThrowIfCancellationRequested();
                throw new ClrModelException(ClrModelErrorKind.Timeout,
                    process.HasExited
                        ? $"Der Hilfsprozess ist beim Start beendet worden (Exit-Code {process.ExitCode})."
                        : $"Der Hilfsprozess hat nach {startTimeout.TotalSeconds:0} s nicht geantwortet und wurde beendet.",
                    host.ErrorOutput);
            }
            catch
            {
                await host.DisposeAsync();
                throw;
            }
        }
        catch when (process is null)
        {
            await pipe.DisposeAsync();
            TryDelete(work);
            throw;
        }
    }

    /// <summary>Completion compiles the script once or twice: a few hundred milliseconds even for a large project.</summary>
    public static readonly TimeSpan CompleteTimeout = TimeSpan.FromSeconds(10);

    public async Task<LinqRunResult> RunAsync(string code, string variables, CancellationToken cancellationToken)
    {
        var response = await RequestAsync(new LinqRequest(LinqProtocol.Run, 0, code, variables), RunTimeout,
            $"Der Code lief länger als {RunTimeout.TotalSeconds:0} s – der Hilfsprozess wurde beendet und startet beim nächsten Mal neu.", cancellationToken);
        return response.Run ?? throw Failure(response);
    }

    public async Task<IReadOnlyList<LinqCompletionItem>> CompleteAsync(string code, string variables, string section, int offset, CancellationToken cancellationToken)
    {
        var response = await RequestAsync(new LinqRequest(LinqProtocol.Complete, 0, code, variables, section, offset), CompleteTimeout,
            $"Die Vorschläge brauchten länger als {CompleteTimeout.TotalSeconds:0} s – der Hilfsprozess wurde beendet und startet beim nächsten Mal neu.", cancellationToken);
        return response.Completion ?? throw Failure(response);
    }

    private static ClrModelException Failure(LinqResponse response) =>
        new(response.Error?.Kind ?? ClrModelErrorKind.HostFailed, response.Error?.Message ?? "Keine Antwort.", response.Error?.Detail);

    /// <summary>One request at a time; the id is assigned here.</summary>
    /// <param name="timeoutMessage">What the user reads if the host does not answer within <paramref name="timeout"/>.</param>
    private async Task<LinqResponse> RequestAsync(LinqRequest request, TimeSpan timeout, string timeoutMessage, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!IsAlive)
            {
                throw new ClrModelException(ClrModelErrorKind.HostFailed, "Der Hilfsprozess der LINQ-Konsole läuft nicht mehr.", ErrorOutput);
            }

            var id = ++_nextId;
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(timeout);
            try
            {
                await _writer.WriteLineAsync(JsonSerializer.Serialize(request with { Id = id }, LinqProtocol.JsonOptions).AsMemory(), limit.Token);
                return await ReadResponseAsync(id, limit.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException)
            {
                // The host may still be busy with the old request: its answers would no longer match. Start afresh.
                Kill();
                cancellationToken.ThrowIfCancellationRequested();
                throw new ClrModelException(ClrModelErrorKind.Timeout,
                    ex is IOException ? "Der Hilfsprozess der LINQ-Konsole ist abgestürzt." : timeoutMessage, ErrorOutput);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private string ErrorOutput
    {
        get
        {
            lock (_errors)
            {
                return _errors.ToString();
            }
        }
    }

    private async Task<LinqResponse> ReadResponseAsync(int id, CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await _reader.ReadLineAsync(cancellationToken) ?? throw new IOException("The model host closed the pipe.");
            var response = JsonSerializer.Deserialize<LinqResponse>(line, LinqProtocol.JsonOptions);
            if (response?.Id == id)
            {
                return response;
            }
        }
    }

    private void Kill()
    {
        _dead = true;
        try
        {
            _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // already exited
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (IsAlive)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _writer.WriteLineAsync(JsonSerializer.Serialize(new LinqRequest(LinqProtocol.Shutdown, 0), LinqProtocol.JsonOptions).AsMemory(), timeout.Token);
                await _process.WaitForExitAsync(timeout.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
            {
                // killed below
            }
        }

        Kill();
        _process.Dispose();
        await _pipe.DisposeAsync();
        _gate.Dispose();
        TryDelete(_work);
    }

    private static void TryDelete(DirectoryInfo directory)
    {
        try
        {
            directory.Delete(recursive: true);
        }
        catch (IOException)
        {
            // a file still in use by a killed process: the temp folder is cleaned up by Windows later
        }
    }
}
