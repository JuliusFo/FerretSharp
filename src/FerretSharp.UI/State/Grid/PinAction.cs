using FerretSharp.UI.Resources;

namespace FerretSharp.UI.State;

/// <summary>
/// The pin action of the cell and the column menu (WP-61): pin, release, or – a primary key column, pinned always – nothing to
/// do. One rule for both menus (review 3.54.0, C6: it stood twice in the markup, where a fix of WP-61 had been made).
/// </summary>
/// <param name="Label">The action's name.</param>
/// <param name="Active">Shown as on (pinned).</param>
/// <param name="Unavailable">Why there is nothing to do; null if there is.</param>
public sealed record PinAction(string Label, bool Active, string? Unavailable)
{
    /// <param name="pinned">The column is pinned now.</param>
    /// <param name="primaryKey">A column of the primary key: pinned with the key, not by the user.</param>
    public static PinAction Of(bool pinned, bool primaryKey) =>
        primaryKey ? new(GridText.ColumnMenu_PkPinned, true, GridText.ColumnMenu_PkPinnedTitle)
        : pinned ? new(GridText.Grid_UnpinColumn, true, null)
        : new(GridText.Grid_PinColumnLeft, false, null);
}
