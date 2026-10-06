namespace FerretSharp.Core.Data;

/// <summary>
/// Which read-only snapshot a grid shows (locked workspaces, ADR 0006): the first page starts a new one, later pages
/// stay in the session's snapshot – which another tab of the same workspace may have replaced meanwhile.
/// </summary>
public static class Snapshots
{
    /// <param name="startRow">First row of the page just read.</param>
    /// <param name="shown">Snapshot of the grid so far (its first page).</param>
    /// <param name="moved">A page from another snapshot was shown already.</param>
    /// <param name="page">Snapshot of the page just read; null outside a read-only transaction.</param>
    /// <returns>The grid's snapshot and whether its pages now come from more than one.</returns>
    public static (DateTimeOffset? Shown, bool Moved) Next(int startRow, DateTimeOffset? shown, bool moved, DateTimeOffset? page) =>
        startRow == 0 || shown is null
            ? (page, startRow != 0 && moved)
            : (shown, moved || (page is not null && page != shown));
}
