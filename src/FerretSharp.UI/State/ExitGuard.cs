namespace FerretSharp.UI.State;

/// <summary>
/// Lets the shell veto closing the window while there are uncommitted changes (v2). The host asks
/// <see cref="CanExit"/> when the window is about to close; if the answer is no, it cancels the close and calls
/// <see cref="RequestExit"/>, the shell asks the user and raises <see cref="ExitApproved"/> once it may close.
/// </summary>
public sealed class ExitGuard
{
    /// <summary>Set by the shell: true if nothing is left to decide.</summary>
    public Func<bool>? CanExit { get; set; }

    /// <summary>Set by the shell: shows the dialog for the uncommitted changes.</summary>
    public Action? RequestExit { get; set; }

    /// <summary>Raised by the shell after commit or rollback; the host then closes the window for real.</summary>
    public event Action? ExitApproved;

    public void ApproveExit() => ExitApproved?.Invoke();
}
