using FerretSharp.Core.Connections;

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
    public const string Moved =
        "Der Workspace ist schreibgeschützt: Seine Session liest aus einem Stand (READ ONLY-Transaktion), den jede neue Abfrage " +
        "– auch in einem anderen Tab dieses Workspaces – neu setzt. Eine später nachgeladene Seite kam deshalb aus einem neueren " +
        "Stand als die erste; hat sich dazwischen etwas geändert, können Zeilen an der Seitengrenze doppelt sein oder fehlen. " +
        "Neu laden holt alles aus einem Stand.";
}
