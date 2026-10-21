using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using FerretSharp.Core;
using FerretSharp.Core.Links;
using FerretSharp.UI.State;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FerretSharp.App.Services;

/// <summary>
/// Hands a link from a second start to the running instance of the same data folder (WP-37, ADR 0026): one named pipe per
/// data folder, for the current Windows user only (<see cref="PipeOptions.CurrentUserOnly"/>); one message per connection,
/// the link as UTF-8, at most <see cref="TableLink.MaxLength"/> characters.
/// </summary>
internal static class LinkPipe
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);

    /// <summary>The pipe of a data folder: its full path hashed (a path may hold characters a pipe name may not).</summary>
    public static string NameOf(AppPaths paths)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(paths.Root).TrimEnd('\\').ToUpperInvariant()));
        return "FerretSharp.Links." + Convert.ToHexString(hash, 0, 12);
    }

    /// <summary>Sends <paramref name="link"/> to the running instance; false if none listens (or it did not take it in time).</summary>
    public static bool TrySend(AppPaths paths, string link)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", NameOf(paths), PipeDirection.Out, PipeOptions.CurrentUserOnly);
            pipe.Connect(ConnectTimeout);
            pipe.Write(Encoding.UTF8.GetBytes(TableLink.ForTransfer(link)));
            pipe.Flush();
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>Listens on <see cref="LinkPipe"/> while FerretSharp runs and posts what arrives to the <see cref="LinkInbox"/>.</summary>
internal sealed class LinkPipeServer(AppPaths paths, LinkInbox inbox, ILogger<LinkPipeServer> logger) : BackgroundService
{
    // A little more than a link may have in UTF-8 (TableLink.ForTransfer sends at most one character more). A message that
    // fills the buffer still decodes to more than TableLink.MaxLength characters (at most 3 bytes each), which the parser refuses.
    private const int MaxBytes = TableLink.MaxLength * 3 + 16;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(LinkPipe.NameOf(paths), PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(stoppingToken);
                var text = await ReadAsync(pipe, stoppingToken);
                // Never the link itself: it may hold values from Prod (like bind values, never in the log).
                logger.LogInformation("Link received from a second start ({Length} characters)", text.Length);
                inbox.Post(text);
                failures = 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                // A sender that went away or never wrote, or the pipe name taken by another process: listen again after a moment.
                // Review 3.40.0, A27: a name taken for good (another user of a terminal server) failed every 2 s, each time
                // with a stack trace in the log – now the wait doubles up to 5 minutes, and a series is logged once.
                failures++;
                if (failures == 1)
                {
                    logger.LogWarning(ex, "Link pipe failed");
                }
                else
                {
                    logger.LogDebug("Link pipe failed again ({Failures} in a row): {Message}", failures, ex.Message);
                }

                await Task.Delay(LinkPipeRetry.Delay(failures), stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task<string> ReadAsync(Stream pipe, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5)); // a sender that connects and never writes does not block the next one
        var buffer = new byte[MaxBytes];
        var length = 0;
        int read;
        while (length < buffer.Length && (read = await pipe.ReadAsync(buffer.AsMemory(length), timeout.Token)) > 0)
        {
            length += read;
        }

        return Encoding.UTF8.GetString(buffer, 0, length);
    }
}
