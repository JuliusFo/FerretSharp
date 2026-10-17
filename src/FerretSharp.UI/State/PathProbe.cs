namespace FerretSharp.UI.State;

/// <summary>
/// A check of typed input that touches the disk – <c>File.Exists</c>, reading a template, the build output of a project –
/// on a pool thread, its answer kept for that input (review 3.40.0, A14). Run in the render, as before, a path on a share
/// behind a silent VPN froze the window at every keystroke (<c>\\s</c>, <c>\\se</c> …). Until the answer is there,
/// <see cref="For"/> says it is not known; a newer input makes an older answer stale.
/// </summary>
/// <param name="probe">The check; runs on a pool thread. An exception counts as answer <c>default</c>.</param>
/// <param name="onUiThread">Runs the given update on the component's thread and renders (<c>Fire</c> of the component).</param>
public sealed class PathProbe<TKey, T>(Func<TKey, T> probe, Action<Action> onUiThread) where TKey : notnull
{
    private TKey? _key;
    private Task<T>? _running;
    private (bool Known, T? Value) _answer;

    /// <summary>The answer for <paramref name="key"/> if known; otherwise starts the check (once per input) and says unknown.</summary>
    public (bool Known, T? Value) For(TKey key)
    {
        if (_running is null || !EqualityComparer<TKey>.Default.Equals(key, _key))
        {
            Start(key);
        }

        return _answer;
    }

    /// <summary>Checks <paramref name="key"/> again and waits for it – on "Save" and "Test", where the answer decides.</summary>
    public async Task<T?> FreshAsync(TKey key)
    {
        var running = Start(key);
        T? value;
        try
        {
            value = await running;
        }
        catch (Exception)
        {
            value = default;
        }

        if (ReferenceEquals(running, _running))
        {
            _answer = (true, value);
        }

        return value;
    }

    private Task<T> Start(TKey key)
    {
        _key = key;
        _answer = (false, default);
        var running = _running = Task.Run(() => probe(key));
        _ = running.ContinueWith(done => onUiThread(() =>
        {
            if (ReferenceEquals(done, _running))
            {
                _answer = (true, done.IsCompletedSuccessfully ? done.Result : default);
            }
        }), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        return running;
    }
}
