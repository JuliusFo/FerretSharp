using FerretSharp.Core.Connections;
using Microsoft.Extensions.Logging;

namespace FerretSharp.UI.State;

/// <summary>What came of a database call from the UI (<see cref="DbCalls.RunDbAsync{T}"/>).</summary>
/// <param name="Value">The result; default unless <see cref="Succeeded"/>.</param>
/// <param name="Error">
/// What went wrong, to show (<see cref="DatabaseException.Display"/>, "Details" with the statement): a database error, or a
/// refusal (<see cref="RefusedException"/>) as one without error code.
/// </param>
/// <param name="Cancelled">Cancelled, or the workspace was closed meanwhile – nobody waits for the result; show nothing.</param>
public readonly record struct DbResult<T>(T? Value, DatabaseException? Error, bool Cancelled)
{
    public bool Succeeded => Error is null && !Cancelled;

    /// <summary>FerretSharp refused (invalid filter, locked workspace …): no statement ran, there are no details to show.</summary>
    public bool IsRefusal => Error?.InnerException is RefusedException;
}

/// <summary>
/// The one way components call the database (R2): off the UI thread, and every failure handled the same – a database
/// error is logged with its statement (binds masked on Prod) and reported, so a lost connection shows the banner; a
/// refusal comes back as error without being logged; cancellation and a closed workspace are silent. Anything else is a
/// bug and goes on to the tab's error boundary.
/// </summary>
public static class DbCalls
{
    /// <param name="logger">The component's logger (the log names where it failed).</param>
    /// <param name="profile">The active connection's profile: bind values of Prod connections are not logged.</param>
    public static Task<DbResult<T>> RunDbAsync<T>(
        this ShellState shell, ILogger logger, ConnectionProfile? profile, Func<Task<T>> call, CancellationToken cancellationToken = default) =>
        shell.CallDbAsync(logger, profile, () => Task.Run(call, cancellationToken));

    /// <summary>
    /// Like <see cref="RunDbAsync{T}"/>, but on the caller's thread: for a sequence that changes UI state between its database
    /// calls (writing, commit – <see cref="WorkspaceEditing"/>). The database calls themselves are asynchronous.
    /// </summary>
    public static async Task<DbResult<T>> CallDbAsync<T>(this ShellState shell, ILogger logger, ConnectionProfile? profile, Func<Task<T>> call)
    {
        try
        {
            return new DbResult<T>(await call(), null, false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or WorkspaceClosedException)
        {
            return new DbResult<T>(default, null, true);
        }
        catch (DatabaseException ex)
        {
            QueryErrorLog.Log(logger, ex, profile);
            shell.ReportFailure(ex);
            return new DbResult<T>(default, ex, false);
        }
        catch (RefusedException ex)
        {
            return new DbResult<T>(default, new DatabaseException(ex.Message, inner: ex), false);
        }
    }

    /// <summary>
    /// For actions without a place of their own to show errors (status bar, menus): a database error opens the error dialog,
    /// a refusal becomes a notice. True if the call succeeded.
    /// </summary>
    public static bool ShowFailure<T>(this ShellState shell, DbResult<T> result)
    {
        if (result.Error is { } error)
        {
            if (result.IsRefusal)
            {
                shell.Notify(error.Message);
            }
            else
            {
                shell.ShowError(error);
            }
        }

        return result.Succeeded;
    }

    /// <inheritdoc cref="RunDbAsync{T}"/>
    public static Task<DbResult<bool>> RunDbAsync(
        this ShellState shell, ILogger logger, ConnectionProfile? profile, Func<Task> call, CancellationToken cancellationToken = default) =>
        shell.RunDbAsync(logger, profile, async () =>
        {
            await call();
            return true;
        }, cancellationToken);
}
