using FerretSharp.Core.Connections;
using FerretSharp.Core.Data;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;
using FerretSharp.Core.Scripts;

namespace FerretSharp.UI.State;

/// <summary>A script of the cell menu, its file read again and its run worked out (WP-84).</summary>
/// <param name="WorkspaceId">The workspace of the table tab it was started from: the SQL tab opens there.</param>
public sealed record ScriptCellStart(Guid WorkspaceId, ScriptMatch Match, ScriptDocument Document, ScriptTargetRun Run)
{
    /// <summary>
    /// The dialog asks for the other parameters first (decision of the user): the script would run at once and some
    /// parameters have no value from the cell. A script that writes only opens, so it needs no dialog.
    /// </summary>
    public bool AsksFirst => Run.ReadOnly && Run.Missing.Count > 0;
}

/// <summary>
/// Starts a script from a cell (WP-84): reads the file again – the menu was built from the library's last read, the file
/// may have changed –, works out the run (<see cref="ScriptTargetRun"/>) and opens the SQL tab, which runs it at once if
/// it only reads.
/// </summary>
public static class ScriptCellLaunch
{
    /// <summary>
    /// The script's file and run for <paramref name="values"/> of <paramref name="column"/> (the clicked cell's first); null
    /// with the reason if it cannot start (the file is gone, NULL, the list does not fit).
    /// </summary>
    public static async Task<(ScriptCellStart? Start, string? Refusal)> PrepareAsync(
        Guid workspaceId, ScriptMatch match, ColumnInfo column, IReadOnlyList<object?> values, ScriptUsage usage, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(match);
        ArgumentNullException.ThrowIfNull(usage);
        ScriptDocument document;
        try
        {
            document = await ScriptFiles.ReadAsync(match.Script.Root.Path, match.Script.Path, cancellationToken);
        }
        catch (RefusedException ex)
        {
            return (null, ex.Message);
        }

        // Off the UI thread: the text comes from a shared folder, spreading a list over it is work (review WP-84, M1).
        var remembered = usage.ValuesOf(match.Script);
        var (run, refusal) = await Task.Run(() =>
        {
            var run = ScriptTargetRun.Of(document.Script.Header, document.Script.Body, match.Target, column, values, remembered, out var refusal);
            return (run, refusal);
        }, cancellationToken);
        return run is null ? (null, refusal) : (new ScriptCellStart(workspaceId, match, document, run), null);
    }

    /// <summary>
    /// Opens the SQL tab; it runs the script if it only reads and <paramref name="run"/> asks for it ("Open in editor" of the
    /// dialog does not). <paramref name="typed"/> holds the dialog's variables for the other parameters (null without the
    /// dialog); their values are kept for the next start, in memory only. Null if the workspace is gone.
    /// </summary>
    public static async Task<SqlTab?> StartAsync(
        ShellState shell, ScriptUsage usage, ScriptCellStart start, IReadOnlyList<SqlVariable>? typed, bool run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(usage);
        ArgumentNullException.ThrowIfNull(start);
        var variables = start.Run.Variables;
        if (typed is not null)
        {
            usage.Remember(start.Match.Script, typed.Select(v => KeyValuePair.Create(v.Name, v.Value)));
            variables = [.. variables.Select(v => typed.FirstOrDefault(t => string.Equals(t.Name, v.Name, StringComparison.OrdinalIgnoreCase)) ?? v)];
        }

        var tab = shell.OpenScriptRun(start.WorkspaceId, start.Match.Script.Root, start.Match.Script.Path, start.Document, variables,
            start.Run.Spread ? start.Run.Text : null, run && start.Run.ReadOnly);
        if (tab is not null)
        {
            await usage.UsedAsync(start.Match.Script, cancellationToken); // a file that cannot be written: the order counts until quitting
        }

        return tab;
    }

    /// <summary>The values a script gets from the menu's cells: the clicked cell's, then those of the other selected rows.</summary>
    public static IReadOnlyList<object?> ValuesOf(RowData clicked, IReadOnlyList<RowData> selected, int column) =>
        [clicked.Values[column], .. selected.Where(r => !ReferenceEquals(r, clicked) && !Equals(r.Key, clicked.Key)).Select(r => r.Values[column])];
}
