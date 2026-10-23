using System.Globalization;
using FerretSharp.Core.Relations;

namespace FerretSharp.UI.Components.Schema;

/// <summary>Inline styles of a laid-out diagram (relations WP-46, records WP-60): positions in CSS pixels, invariant.</summary>
internal static class DiagramStyle
{
    public static string Px(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);

    public static string Size(double width, double height) => $"width: {Px(width)}px; height: {Px(height)}px;";

    public static string Box(NodeBox box) => $"left: {Px(box.X)}px; top: {Px(box.Y)}px; {Size(box.Width, box.Height)}";

    /// <summary>The drawing at its own size, scaled by the zoom (the scroll area around it has the scaled size).</summary>
    public static string Stage(DiagramPlacement placement, double zoom) =>
        $"{Size(placement.Width, placement.Height)} transform: scale({zoom.ToString(CultureInfo.InvariantCulture)});";
}
