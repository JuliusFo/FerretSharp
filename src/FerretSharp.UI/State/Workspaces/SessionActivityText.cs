using FerretSharp.Core.Data;
using FerretSharp.Core.Oracle;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Settings;
using FerretSharp.UI.Resources;

namespace FerretSharp.UI.State;

/// <summary>A workspace of this instance whose session holds a lock (WP-52): named, with a way there.</summary>
/// <param name="Connection">The connection's label when it is not the one of the waiting statement; null for the same.</param>
public sealed record OwnWorkspace(Guid ConnectionId, Guid WorkspaceId, string Name, string? Connection);

/// <summary>The lines <c>RunProgress</c> shows of a <see cref="SessionActivity"/> (WP-52).</summary>
public static class SessionActivityText
{
    /// <summary>The workspace of this instance whose session carries the tag; labelled with its connection if that is another one.</summary>
    public static OwnWorkspace? OwnOf(ConnectionHub hub, string? tag, Guid connectionId, AppSettings settings) =>
        hub.FindSession(tag) is var (scope, workspace)
            ? new OwnWorkspace(scope.Id, workspace.Id, workspace.Name,
                scope.Id == connectionId || scope.Profile is not { } profile ? null : ConnectionLabel.Of(profile, settings))
            : null;

    /// <summary>"Waits for a lock on KUNDEN held by your workspace “Bug 3711” · 0:12" (how long it waits); null if it does not wait.</summary>
    public static string? Wait(SessionWait? wait, OwnWorkspace? own)
    {
        if (wait is null)
        {
            return null;
        }

        var holder = own is not null ? Own(own) : Session(wait);
        var text = wait.Object is { } table
            ? TextFormat.Format(SqlEditorText.Run_WaitsOn, table.Name, holder)
            : TextFormat.Format(SqlEditorText.Run_Waits, holder);
        return wait.For is { } waited ? $"{text} · {TimeText.Clock(waited)}" : text;
    }

    /// <summary>"Table Scan APP.KUNDEN: 43 % · about 20 s left"; null without a long operation.</summary>
    public static string? Progress(LongOperation? operation)
    {
        if (operation is null)
        {
            return null;
        }

        var text = operation.Target is { Length: > 0 } target
            ? TextFormat.Format(SqlEditorText.Run_ProgressOn, operation.Operation, target, operation.Percent)
            : TextFormat.Format(SqlEditorText.Run_Progress, operation.Operation, operation.Percent);
        return operation.Remaining is { } left && left > TimeSpan.Zero
            ? TextFormat.Format(SqlEditorText.Run_ProgressLeft, text, TimeText.Duration(left))
            : text;
    }

    /// <summary>
    /// Without rights on the V$ views: the other workspaces with uncommitted writes, which a writing statement may wait for
    /// (“Bug 3711”, “Workspace 2”); null if there are none.
    /// </summary>
    public static string? OpenElsewhere(IReadOnlyList<string> workspaces) => workspaces.Count == 0
        ? null
        : TextFormat.Plural(workspaces.Count, SqlEditorText.Run_OpenChangesOne, SqlEditorText.Run_OpenChangesOther,
            string.Join(", ", workspaces.Select(name => TextFormat.Format(SqlEditorText.Run_QuotedName, name))));

    private static string Own(OwnWorkspace own) => own.Connection is { } connection
        ? TextFormat.Format(SqlEditorText.Run_HolderOwnElsewhere, own.Name, connection)
        : TextFormat.Format(SqlEditorText.Run_HolderOwn, own.Name);

    /// <summary>
    /// The program of a session as the run progress and the lock holders name it: another FerretSharp with its workspace,
    /// else its module (what SQL Developer and EF apps set), else its program (R6b: was written in both).
    /// </summary>
    public static string? ProgramOf(LockHolder holder)
    {
        ArgumentNullException.ThrowIfNull(holder);
        return holder.Module == OracleSessionDefaults.Module
            ? TextFormat.Format(GridText.Problem_FerretSharpWorkspace, holder.Action)
            : holder.Module ?? holder.Program;
    }

    /// <summary>"session 123 (SCOTT, PC-17, SQL Developer)"; a FerretSharp of someone else with its workspace.</summary>
    private static string Session(SessionWait wait)
    {
        if (wait.Blocker is not { } blocker)
        {
            return TextFormat.Format(SqlEditorText.Run_HolderSessionOnly, wait.BlockerSid);
        }

        var details = new[] { blocker.User, blocker.Machine, ProgramOf(blocker) }.Where(part => !string.IsNullOrWhiteSpace(part));
        return TextFormat.Format(SqlEditorText.Run_HolderSession, blocker.Sid, string.Join(", ", details));
    }
}
