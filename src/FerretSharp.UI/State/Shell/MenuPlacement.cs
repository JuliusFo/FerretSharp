namespace FerretSharp.UI.State;

/// <summary>
/// Where a menu opens and how large the window is – PopoverMenu keeps the menu inside it (R6b, review 3.40.0, B4: was
/// worked out in six components).
/// </summary>
public sealed record MenuPlacement(double X, double Y, double ViewportWidth, double ViewportHeight)
{
    /// <summary>Distance a menu keeps from the left edge of the window.</summary>
    public const double Margin = 8;

    /// <summary>
    /// Below the mouse: the menu ends <paramref name="shiftX"/> right of it (at <c>clientX + shiftX</c>) and starts
    /// <paramref name="shiftY"/> below it, never closer than <see cref="Margin"/> to the left edge.
    /// </summary>
    public static MenuPlacement Below(
        double clientX, double clientY, double menuWidth, double shiftX, double shiftY, double viewportWidth, double viewportHeight) =>
        new(Math.Max(Margin, clientX - menuWidth + shiftX), clientY + shiftY, viewportWidth, viewportHeight);
}
