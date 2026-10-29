namespace FerretSharp.UI.State;

/// <summary>
/// Cancels work that may sit in ODP.NET on a pool thread: <c>OracleCommand.Cancel()</c> sends the break synchronously, and
/// over a silent VPN it blocks (review 3.40.0, A3). The source stays with its owner, who frees it once the work ended – freed
/// here, a cancel still on its way found it gone and the work went on (review 3.54.0, A3, B10).
/// </summary>
public static class CancelOffThread
{
    public static void Run(CancellationTokenSource? source)
    {
        if (source is null)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                source.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // the work ended meanwhile, and freed its source
            }
        });
    }
}
