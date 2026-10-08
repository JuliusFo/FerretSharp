using FerretSharp.Core.Connections;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;
using FerretSharp.Core.Workspaces;
using Microsoft.Extensions.Logging;

namespace FerretSharp.UI.State;

/// <summary>Where the count of an incoming FK jump stands.</summary>
public abstract record FkCount
{
    public sealed record Loading : FkCount;

    public sealed record Done(long Count) : FkCount;

    /// <summary>Gave up after <see cref="FkNavigation.CountTimeout"/> (FK columns are often not indexed).</summary>
    public sealed record TimedOut : FkCount;

    public sealed record Failed(string Message) : FkCount;
}

/// <summary>
/// Counts the rows behind incoming FK jumps, one after the other on the workspace's session (the context menu of the
/// grid and the form, WP-21). Cancelling – the menu closes, the form moves to another row – stops the rest.
/// </summary>
public sealed class FkCounts
{
    private readonly Dictionary<FkJump, FkCount> _states = [];

    public FkCount? Of(FkJump jump) => _states.GetValueOrDefault(jump);

    /// <param name="changed">After each count (on any thread: the caller re-renders through InvokeAsync).</param>
    public async Task CountAsync(
        ShellState shell, ILogger logger, ActiveConnection active, WorkspaceManager workspaces, Guid workspaceId,
        IReadOnlyList<FkJump> jumps, Action changed, CancellationToken token)
    {
        if (active.Schema is not { } schema)
        {
            return;
        }

        _states.Clear();
        foreach (var jump in jumps.Where(j => j.IsAvailable))
        {
            _states[jump] = new FkCount.Loading();
        }

        foreach (var jump in _states.Keys.ToList())
        {
            var result = await shell.RunDbAsync(logger, active, async () =>
            {
                var target = await schema.GetDetailsAsync(FkTargets.SummaryOf(active, jump.Table), token);
                var data = await workspaces.GetDataAsync(workspaceId, token);
                return await FkNavigation.CountAsync(data, target, jump, FkNavigation.CountTimeout, token);
            }, token);
            if (result.Cancelled || token.IsCancellationRequested)
            {
                return;
            }

            _states[jump] = result.Error is { } error ? new FkCount.Failed(error.Display)
                : result.Value is { } n ? new FkCount.Done(n)
                : new FkCount.TimedOut();
            changed();
        }
    }
}

/// <summary>Targets of FK jumps: how to open and label them (context menu, form).</summary>
public static class FkTargets
{
    /// <summary>Tables outside the browsed schemas (e.g. a referencing table in another schema) are opened by name.</summary>
    public static TableSummary SummaryOf(ActiveConnection active, TableRef table) =>
        active.Schema?.Find(table) ?? new TableSummary(table.Owner, table.Name, TableKind.Table);

    /// <summary>The name the user knows: without owner in the browsed schema (or through a synonym), qualified otherwise.</summary>
    public static string Label(ActiveConnection active, TableRef table)
    {
        var summary = SummaryOf(active, table);
        return summary.Synonym is not null || table.Owner == active.Schema?.Owner ? summary.DisplayName : table.ToString();
    }

    public static string Tooltip(FkJump jump) =>
        $"{jump.ForeignKey.Name}: {jump.ForeignKey.From}({string.Join(", ", jump.ForeignKey.FromColumns)}) → " +
        $"{jump.ForeignKey.To}({string.Join(", ", jump.ForeignKey.ToColumns)})" +
        (jump.ForeignKey.Source == FkSource.ClrModel ? Environment.NewLine + "Beziehung aus dem C#-Modell – in der Datenbank gibt es dafür keinen Constraint." : "") +
        (jump.SkippedNote is { } skipped ? Environment.NewLine + skipped + "." : "") +
        (jump.Unavailable is { } reason ? Environment.NewLine + reason : "");
}
