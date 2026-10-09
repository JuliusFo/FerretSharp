using FerretSharp.Core.Connections;
using FerretSharp.UI.Resources;

namespace FerretSharp.UI.State;

/// <summary>State of a result grid (LINQ console) for the footer below it.</summary>
/// <param name="AllLoaded">The last page has arrived.</param>
/// <param name="DataAsOf">Start of the read-only snapshot of the first page (locked workspaces).</param>
/// <param name="SnapshotMoved">A later page came from another snapshot (see <see cref="SnapshotTexts.Moved"/>).</param>
public sealed record SqlGridStatus(
    bool Loading, int RowsLoaded, bool AllLoaded, TimeSpan? Elapsed, DateTimeOffset? DataAsOf, DatabaseException? Error, bool SnapshotMoved = false);

public static class SnapshotTexts
{
    /// <summary>Tooltip of the footer hint when the pages of a grid come from more than one read-only snapshot.</summary>
    public static string Moved => SqlEditorText.Snapshot_Moved;
}
