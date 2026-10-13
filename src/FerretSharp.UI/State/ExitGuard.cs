namespace FerretSharp.UI.State;

/// <summary>
/// Lets the shell veto closing the window while there are uncommitted changes (v2). The host asks
/// <see cref="CanExit"/> when the window is about to close; if the answer is no, it cancels the close and calls
/// <see cref="RequestExit"/>, the shell asks the user and raises <see cref="ExitApproved"/> once it may close.
/// The app can close itself the same way (<see cref="RequestClose"/>, the restart into an update, WP-32).
/// </summary>
public sealed class ExitGuard
{
    private readonly Lock _lock = new();
    private Action? _onExit;

    /// <summary>Set by the shell: true if nothing is left to decide.</summary>
    public Func<bool>? CanExit { get; set; }

    /// <summary>Set by the shell: shows the dialog for the uncommitted changes.</summary>
    public Action? RequestExit { get; set; }

    /// <summary>Raised by the shell after commit or rollback; the host then closes the window for real.</summary>
    public event Action? ExitApproved;

    /// <summary>Raised by <see cref="RequestClose"/>: the host closes the window as its close button would.</summary>
    public event Action? CloseRequested;

    public void ApproveExit() => ExitApproved?.Invoke();

    /// <summary>
    /// Closes the app with the same question as the window's close button. <paramref name="onExit"/> runs once the app
    /// really ends from this request; if the user cancels the question and closes later themselves, it does not.
    /// </summary>
    public void RequestClose(Action onExit)
    {
        ArgumentNullException.ThrowIfNull(onExit);
        lock (_lock)
        {
            _onExit = onExit;
        }

        CloseRequested?.Invoke();
    }

    /// <summary>Host: the user closes the window (not <see cref="RequestClose"/>) – what a cancelled request left behind is dropped.</summary>
    public void UserClosing()
    {
        lock (_lock)
        {
            _onExit = null;
        }
    }

    /// <summary>Host: the window closes for real; runs what <see cref="RequestClose"/> asked for.</summary>
    public void Exiting()
    {
        Action? onExit;
        lock (_lock)
        {
            (onExit, _onExit) = (_onExit, null);
        }

        onExit?.Invoke();
    }
}
