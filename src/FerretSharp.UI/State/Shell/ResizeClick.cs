namespace FerretSharp.UI.State;

/// <summary>
/// Whether a click on a drag handle (<c>ResizeHandle</c>) asks for the default size: a double click – <c>Detail</c> 2 or
/// more, there is no <c>dblclick</c> in the WPF host (<c>ui.md</c>) – where the button went down. The click that ends a
/// drag counts on too when its press followed a click (detail 2, and 3 for the host's duplicated mouse-down), but it lands
/// where the pointer was released (review 2026-11-04: a click and a drag right after it reset the size just dragged).
/// </summary>
public static class ResizeClick
{
    /// <summary>Pixels the pointer moves before a press becomes a drag; the same as <c>dragThreshold</c> in dom.js.</summary>
    public const double DragThreshold = 3;

    /// <param name="press">Where the last press on the handle was; null if none was seen.</param>
    public static bool IsReset(long detail, long button, (double X, double Y)? press, double x, double y) =>
        detail >= 2 && button == 0
        && (press is not { } at || (Math.Abs(x - at.X) < DragThreshold && Math.Abs(y - at.Y) < DragThreshold));
}
