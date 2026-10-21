using System.Globalization;
using FerretSharp.Core.Resources;
using FerretSharp.UI.Resources;

namespace FerretSharp.UI.State;

/// <summary>
/// The rows selected in a grid, as grid.js reports them for the footer.
/// </summary>
/// <param name="Count">Selected rows; only loaded rows can be selected.</param>
/// <param name="ToEnd">Rows still to load are selected when they arrive (after Ctrl+A or a selection up to the last row).</param>
public sealed record GridSelection(int Count, bool ToEnd)
{
    public static readonly GridSelection None = new(0, false);

    /// <summary>
    /// The footer's text; null for no row or one (a click always selects one, saying so would be noise).
    /// <paramref name="allLoaded"/>: every row of the grid is loaded – then "loaded rows only" means nothing.
    /// </summary>
    public string? FooterText(bool allLoaded) => Count < 2
        ? null
        : TextFormat.Format(LoadedOnly(allLoaded) ? GridText.Footer_SelectedLoadedRows : GridText.Footer_SelectedRows,
            Count.ToString("N0", CultureInfo.CurrentCulture));

    /// <summary>Its tooltip: why only loaded rows count and what that means for deleting and copying; null if it does not apply.</summary>
    public string? FooterTitle(bool allLoaded) => Count >= 2 && LoadedOnly(allLoaded) ? GridText.Footer_SelectedLoadedRowsTitle : null;

    private bool LoadedOnly(bool allLoaded) => ToEnd && !allLoaded;
}

/// <summary>A grid whose selection the footer shows (<c>FerretGrid</c>, <c>SqlResultGrid</c>).</summary>
public interface ISelectionGrid
{
    GridSelection Selection { get; }

    /// <summary>The selection changed; may be raised outside the renderer's thread.</summary>
    event Action? SelectionChanged;
}
