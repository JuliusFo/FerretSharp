namespace FerretSharp.UI.State;

/// <summary>
/// When to tell the user that WPF's render thread failed (<c>UCEERR_RENDERTHREADFAILURE</c>): Windows resets the graphics
/// output, e.g. when the laptop wakes up at a dock, and WPF reports it for every window message that meets the render thread
/// in that moment – several in a row, and a modal dialog causes more (it was one error dialog after the other). The app
/// usually keeps working, so the user is asked once per series whether to restart; repeats are only logged.
/// </summary>
/// <param name="time">The clock of <see cref="Quiet"/> (tests); the system's if null.</param>
public sealed class RenderFailureNotice(TimeProvider? time = null)
{
    /// <summary><c>UCEERR_RENDERTHREADFAILURE</c>, the HRESULT of the <c>COMException</c> WPF throws.</summary>
    public const int RenderThreadFailure = unchecked((int)0x88980406);

    /// <summary>A failure this long after the previous one starts a new series and is asked about again.</summary>
    public static readonly TimeSpan Quiet = TimeSpan.FromMinutes(5);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Lock _lock = new();
    private DateTimeOffset? _last;
    private bool _asking;

    /// <summary>Failures since the user was last asked (logged, not asked about).</summary>
    public int Repeats { get; private set; }

    /// <summary>The exception (or one it wraps) is a failed render thread.</summary>
    public static bool Matches(Exception? exception)
    {
        for (; exception is not null; exception = exception.InnerException)
        {
            if (exception.HResult == RenderThreadFailure)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A failure occurred. True if the user is to be asked now: no question is open and the last failure was a while ago.</summary>
    public bool Report()
    {
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            var ask = !_asking && (_last is not { } last || now - last >= Quiet);
            _last = now;
            if (ask)
            {
                _asking = true;
                Repeats = 0;
            }
            else
            {
                Repeats++;
            }

            return ask;
        }
    }

    /// <summary>The question was answered (or could not be shown): failures from now on count from here.</summary>
    public void Answered()
    {
        lock (_lock)
        {
            _asking = false;
            _last = _time.GetUtcNow();
        }
    }
}
