using FerretSharp.Core.Schema;
using Microsoft.AspNetCore.Components;

namespace FerretSharp.UI.Components;

/// <summary>Small inline SVG icons; they inherit the text color via <c>currentColor</c>.</summary>
internal static class Icons
{
    public static readonly MarkupString Search = Svg(
        """<circle cx="7" cy="7" r="4.5"/><path d="m10.5 10.5 3 3"/>""", 1.6);

    public static readonly MarkupString Table = Svg(
        """<rect x="2" y="2.5" width="12" height="11" rx="2"/><path d="M2 6.5h12M6.5 6.5v7"/>""");

    public static readonly MarkupString View = Svg(
        """<path d="M1.5 8s2.5-4.5 6.5-4.5S14.5 8 14.5 8 12 12.5 8 12.5 1.5 8 1.5 8Z"/><circle cx="8" cy="8" r="2"/>""");

    public static readonly MarkupString MaterializedView = Svg(
        """<path d="m8 2 6 3-6 3-6-3 6-3Z"/><path d="m2 8 6 3 6-3M2 11l6 3 6-3"/>""");

    public static readonly MarkupString Refresh = Svg(
        """<path d="M13.5 8a5.5 5.5 0 1 1-1.6-3.9"/><path d="M13.5 2.5v3h-3"/>""", 1.6);

    public static readonly MarkupString Home = Svg(
        """<path d="M2.5 7.5 8 3l5.5 4.5"/><path d="M4 6.5V13h8V6.5"/>""", 1.6);

    public static readonly MarkupString Settings = Svg(
        """<path d="M2 4.5h6.5M12.5 4.5H14M2 11.5h1.5M7.5 11.5H14"/><circle cx="10.5" cy="4.5" r="2"/><circle cx="5.5" cy="11.5" r="2"/>""", 1.6);

    public static readonly MarkupString Code = Svg(
        """<path d="M5.5 4 2 8l3.5 4M10.5 4 14 8l-3.5 4"/>""", 1.6);

    public static readonly MarkupString Key = Svg(
        """<circle cx="5.5" cy="10.5" r="3"/><path d="m7.6 8.4 5.9-5.9M11.5 4.5l1.5 1.5"/>""");

    public static MarkupString For(TableKind kind) => kind switch
    {
        TableKind.View => View,
        TableKind.MaterializedView => MaterializedView,
        _ => Table,
    };

    private static MarkupString Svg(string paths, double strokeWidth = 1.4) =>
        new($"""<svg class="icon" width="14" height="14" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="{strokeWidth.ToString(System.Globalization.CultureInfo.InvariantCulture)}" stroke-linecap="round" stroke-linejoin="round">{paths}</svg>""");
}
