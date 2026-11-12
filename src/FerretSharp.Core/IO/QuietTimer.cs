namespace FerretSharp.Core.IO;

/// <summary>
/// Calls back once changes have stopped for a quiet period: each <see cref="Touch"/> starts it anew. For the file watchers
/// of the build output, the display columns and the SQL scripts – a save or a build writes many events (review 3.70.0, B3:
/// three copies of it).
/// </summary>
/// <param name="elapsed">Called on a thread-pool thread (or the fake clock's).</param>
public sealed class QuietTimer(TimeSpan quiet, Action elapsed, TimeProvider? time = null) : IDisposable
{
    private readonly ITimer _timer = (time ?? TimeProvider.System).CreateTimer(_ => elapsed(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

    public TimeSpan Quiet => quiet;

    /// <summary>(Re)starts the quiet period.</summary>
    public void Touch()
    {
        try
        {
            _timer.Change(quiet, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // an event that arrived while disposing
        }
    }

    public void Dispose() => _timer.Dispose();
}
