namespace FerretSharp.Core.Oracle;

/// <summary>
/// The one way to an <see cref="OracleSession"/>'s connection (R3b, out of the session so it can be tested without a
/// database): one caller at a time, because <c>OracleConnection</c> is not thread-safe. Closing lets go of everyone at
/// once – callers still waiting and the one whose work runs – with an <see cref="OperationCanceledException"/>. Work that
/// ignores its cancel (a VPN that went silent) keeps the gate until it ends, so nothing else touches the connection
/// meanwhile; its caller is let go anyway. Neither the semaphore nor the closing token is ever disposed: callers may still
/// be waiting on them.
/// </summary>
/// <param name="translate">Turns an error of the work into what callers see (the session's Oracle error translation); null keeps it.</param>
/// <param name="released">Runs each time the gate is released (the session notes the round trip).</param>
internal sealed class SessionGate(Func<Exception, Exception?> translate, Action? released = null)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _closing = new();
    private volatile bool _closed;

    public bool IsClosed => _closed;

    /// <summary>Work is running (or abandoned work still holds the gate).</summary>
    public bool IsBusy => _gate.CurrentCount == 0;

    /// <summary>
    /// Takes the gate, runs <paramref name="work"/>, translates its errors and releases the gate. Once closing has begun,
    /// the call is abandoned like a cancelled one – also when it was already waiting or its work is running.
    /// </summary>
    public async Task<T> RunAsync<T>(Func<Task<T>> work, CancellationToken cancellationToken)
    {
        if (_closed)
        {
            throw Closed();
        }

        using (var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closing.Token))
        {
            try
            {
                await _gate.WaitAsync(waiting.Token);
            }
            catch (OperationCanceledException) when (_closing.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw Closed();
            }
        }

        Task<T>? running = null;
        var abandoned = false;
        try
        {
            if (_closed)
            {
                throw Closed();
            }

            running = work();
            return await running.WaitAsync(_closing.Token);
        }
        catch (OperationCanceledException) when (running is { IsCompleted: false } && _closing.IsCancellationRequested)
        {
            // Closed while the work hangs (it ignores the cancel): the caller is let go now; the work keeps the gate until
            // it ends, so nothing else touches the connection meanwhile.
            abandoned = true;
            _ = running.ContinueWith(finished =>
            {
                _ = finished.Exception; // observed: the session is gone, nobody waits for it
                Release();
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            throw Closed();
        }
        catch (Exception ex) when (_closed && ex is not OperationCanceledException)
        {
            // Closed while the work ran: whatever the driver throws now is the closing itself, not a lost connection.
            throw new OperationCanceledException("The session was closed.", ex);
        }
        catch (Exception ex) when (translate(ex) is { } translated)
        {
            throw translated;
        }
        finally
        {
            if (!abandoned)
            {
                Release();
            }
        }
    }

    /// <summary>
    /// Starts closing: every caller – later ones, waiting ones and the one whose work runs – gets an
    /// <see cref="OperationCanceledException"/> at once. Then cancel the running work and <see cref="EnterAfterCloseAsync"/>.
    /// </summary>
    public Task BeginCloseAsync()
    {
        _closed = true;
        return _closing.CancelAsync();
    }

    /// <summary>
    /// After <see cref="BeginCloseAsync"/>: waits up to <paramref name="wait"/> for running work to end. True: the caller
    /// now holds the gate (nothing can use the connection any more) and calls <see cref="Release"/> when done. False: work
    /// that ignores its cancel still holds it.
    /// </summary>
    public Task<bool> EnterAfterCloseAsync(TimeSpan wait) => _gate.WaitAsync(wait);

    public void Release()
    {
        released?.Invoke();
        _gate.Release();
    }

    public static OperationCanceledException Closed() => new("The session was closed.");
}
