using FerretSharp.Core.Schema;
using Microsoft.AspNetCore.Components;

namespace FerretSharp.UI.Components;

/// <summary>Small inline SVG icons; they inherit the text color via <c>currentColor</c>.</summary>
internal static class Icons
{
    public static readonly MarkupString Search = Svg(
        """<circle cx="7" cy="7" r="4.5"/><path d="m10.5 10.5 3 3"/>""", 1.6);

    /// <summary>Small cross: clears a search field.</summary>
    public static readonly MarkupString Clear = Svg(
        """<path d="m4.5 4.5 7 7M11.5 4.5l-7 7"/>""", 1.6);

    /// <summary>Two sheets: copy to the clipboard.</summary>
    public static readonly MarkupString Copy = Svg(
        """<rect x="5.5" y="5.5" width="8" height="8" rx="1.5"/><path d="M10.5 5.5v-2a1 1 0 0 0-1-1h-6a1 1 0 0 0-1 1v6a1 1 0 0 0 1 1h2"/>""");

    /// <summary>Tick: done (copied).</summary>
    public static readonly MarkupString Check = Svg(
        """<path d="m3.5 8.5 3 3 6-7"/>""", 1.8);

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

    /// <summary>Cylinder: the SQL editor (WP-17).</summary>
    public static readonly MarkupString Database = Svg(
        """<ellipse cx="8" cy="4" rx="5.5" ry="2"/><path d="M2.5 4v8c0 1.1 2.5 2 5.5 2s5.5-.9 5.5-2V4M2.5 8c0 1.1 2.5 2 5.5 2s5.5-.9 5.5-2"/>""");

    /// <summary>Two columns with arrows between them: the schema comparison (WP-20).</summary>
    public static readonly MarkupString Compare = Svg(
        """<rect x="1.5" y="2.5" width="4.5" height="11" rx="1"/><rect x="10" y="2.5" width="4.5" height="11" rx="1"/><path d="M7 6h2.5M8.5 5l1 1-1 1M9 10H6.5M7.5 9l-1 1 1 1"/>""");

    /// <summary>Open padlock: a workspace unlocked for writing (WP-10).</summary>
    public static readonly MarkupString Unlocked = Svg(
        """<rect x="3" y="7.5" width="10" height="6.5" rx="1.5"/><path d="M5.5 7.5V5a2.5 2.5 0 0 1 4.9-.7"/>""", 1.6);

    public static MarkupString For(TableKind kind) => kind switch
    {
        TableKind.View => View,
        TableKind.MaterializedView => MaterializedView,
        _ => Table,
    };

    /// <summary>Box: a PL/SQL package (WP-28).</summary>
    public static readonly MarkupString Package = Svg(
        """<path d="m8 1.5 5.5 3v7L8 14.5l-5.5-3v-7L8 1.5Z"/><path d="M2.5 4.5 8 7.5l5.5-3M8 7.5v7"/>""");

    /// <summary>Triangle: a stored procedure.</summary>
    public static readonly MarkupString Procedure = Svg(
        """<path d="M4.5 3v10l8-5-8-5Z"/>""");

    /// <summary>ƒ: a stored function.</summary>
    public static readonly MarkupString Function = Svg(
        """<path d="M11 2.5c-2 0-3 1-3.3 3L6.3 11c-.4 1.8-1.3 2.5-3 2.5M5 6.5h6"/>""", 1.5);

    /// <summary>Lightning: a trigger.</summary>
    public static readonly MarkupString Trigger = Svg(
        """<path d="M9 1.5 3.5 9h4l-1 5.5L12.5 7h-4l.5-5.5Z"/>""");

    public static MarkupString For(PlSqlKind kind) => kind switch
    {
        PlSqlKind.Package => Package,
        PlSqlKind.Procedure => Procedure,
        PlSqlKind.Function => Function,
        _ => Trigger,
    };

    private static MarkupString Svg(string paths, double strokeWidth = 1.4) =>
        new($"""<svg class="icon" width="14" height="14" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="{strokeWidth.ToString(System.Globalization.CultureInfo.InvariantCulture)}" stroke-linecap="round" stroke-linejoin="round">{paths}</svg>""");
}
