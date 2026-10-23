using System.Globalization;
using FerretSharp.Core.Relations;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Schema;
using FerretSharp.UI.Resources;

namespace FerretSharp.UI.Components.Schema;

/// <summary>
/// How a laid-out diagram is drawn (relations WP-46, records WP-60, the copy preview): positions in CSS pixels, invariant;
/// the colour and name of a kind of relationship; the tooltip of an edge; what stands out while the mouse rests on a node.
/// </summary>
internal static class DiagramStyle
{
    public static string Px(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);

    public static string Size(double width, double height) => $"width: {Px(width)}px; height: {Px(height)}px;";

    public static string Box(NodeBox box) => $"left: {Px(box.X)}px; top: {Px(box.Y)}px; {Size(box.Width, box.Height)}";

    /// <summary>The drawing at its own size, scaled by the zoom (the scroll area around it has the scaled size).</summary>
    public static string Stage(DiagramPlacement placement, double zoom) =>
        $"{Size(placement.Width, placement.Height)} transform: scale({zoom.ToString(CultureInfo.InvariantCulture)});";

    /// <summary>The class that colours an edge or a swatch of the legend (`--accent`, `--clr`, `--warn`).</summary>
    public static string KindClass(FkSource source) => source switch
    {
        FkSource.Declared => "declared",
        FkSource.ClrModel => "clr",
        _ => "other",
    };

    /// <summary>A kind of relationship in the legend.</summary>
    public static string SourceLabel(FkSource source) => source switch
    {
        FkSource.Declared => SchemaViewText.Relations_SourceDeclared,
        FkSource.ClrModel => SchemaViewText.Relations_SourceClrModel,
        FkSource.Manual => SchemaViewText.Relations_SourceManual,
        _ => SchemaViewText.Relations_SourceConvention,
    };

    /// <param name="label">The name of a table as the diagram shows it.</param>
    public static string EdgeTitle(ForeignKeyInfo relation, Func<TableRef, string> label) => TextFormat.Format(SchemaViewText.Relations_EdgeTitle,
        relation.Name, label(relation.From), string.Join(", ", relation.FromColumns), label(relation.To), string.Join(", ", relation.ToColumns),
        relation.Source == FkSource.Declared ? SchemaViewText.Relations_KindDeclared : SourceLabel(relation.Source));

    /// <summary>While the mouse rests on the node <paramref name="hoverId"/>, its edges stand out and the rest fades.</summary>
    public static string EdgeState(EdgePath edge, string? hoverId) =>
        hoverId is null ? ""
        : edge.From == hoverId || edge.To == hoverId ? "lit"
        : "faded";

    /// <param name="lit">The neighbours of the node under the mouse.</param>
    public static string NodeState<T>(T node, T? hover, IReadOnlySet<T> lit) where T : class =>
        hover is null || hover.Equals(node) ? "" : lit.Contains(node) ? "lit" : "faded";
}
