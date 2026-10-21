namespace FerretSharp.Testing;

/// <summary>
/// Records progress steps synchronously (<see cref="Progress{T}"/> would post them to the thread pool), safe from any thread.
/// One for every test project (review 3.44.0, C6: four copies of it).
/// </summary>
internal sealed class StepRecorder(Action<string>? onReport = null) : IProgress<string>
{
    private readonly List<string> _steps = [];

    /// <summary>The steps so far, in order.</summary>
    public IReadOnlyList<string> All
    {
        get
        {
            lock (_steps)
            {
                return [.. _steps];
            }
        }
    }

    public void Report(string value)
    {
        lock (_steps)
        {
            _steps.Add(value);
        }

        onReport?.Invoke(value);
    }
}
