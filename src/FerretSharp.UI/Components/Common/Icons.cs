using FerretSharp.Core.Schema;
using Microsoft.AspNetCore.Components;

namespace FerretSharp.UI.Components;

/// <summary>
/// Small inline SVG icons; they inherit the text color via <c>currentColor</c>. The shapes are taken unchanged from
/// Lucide (<c>lucide-static</c> 1.55.0, ISC, see <c>wwwroot/lib/lucide</c>) – the Lucide name stands in each summary.
/// A new icon comes from the same set (ADR 0035).
/// </summary>
internal static class Icons
{
    /// <summary>Lucide <c>search</c>.</summary>
    public static readonly MarkupString Search = Svg(
        """<path d="m21 21-4.34-4.34"/><circle cx="11" cy="11" r="8"/>""");

    /// <summary>Lucide <c>x</c>: clears a search field.</summary>
    public static readonly MarkupString Clear = Svg(
        """<path d="M18 6 6 18"/><path d="m6 6 12 12"/>""");

    /// <summary>Lucide <c>copy</c>: copy to the clipboard.</summary>
    public static readonly MarkupString Copy = Svg(
        """<rect width="14" height="14" x="8" y="8" rx="2" ry="2"/><path d="M4 16c-1.1 0-2-.9-2-2V4c0-1.1.9-2 2-2h10c1.1 0 2 .9 2 2"/>""");

    /// <summary>Lucide <c>check</c>: done (copied).</summary>
    public static readonly MarkupString Check = Svg(
        """<path d="M20 6 9 17l-5-5"/>""");

    /// <summary>Lucide <c>table</c>.</summary>
    public static readonly MarkupString Table = Svg(
        """<path d="M12 3v18"/><rect width="18" height="18" x="3" y="3" rx="2"/><path d="M3 9h18"/><path d="M3 15h18"/>""");

    /// <summary>Lucide <c>eye</c>: a view.</summary>
    public static readonly MarkupString View = Svg(
        """<path d="M2.062 12.348a1 1 0 0 1 0-.696 10.75 10.75 0 0 1 19.876 0 1 1 0 0 1 0 .696 10.75 10.75 0 0 1-19.876 0"/><circle cx="12" cy="12" r="3"/>""");

    /// <summary>Lucide <c>layers</c>: a materialized view.</summary>
    public static readonly MarkupString MaterializedView = Svg(
        """<path d="M12.83 2.18a2 2 0 0 0-1.66 0L2.6 6.08a1 1 0 0 0 0 1.83l8.58 3.91a2 2 0 0 0 1.66 0l8.58-3.9a1 1 0 0 0 0-1.83z"/><path d="M2 12a1 1 0 0 0 .58.91l8.6 3.91a2 2 0 0 0 1.65 0l8.58-3.9A1 1 0 0 0 22 12"/><path d="M2 17a1 1 0 0 0 .58.91l8.6 3.91a2 2 0 0 0 1.65 0l8.58-3.9A1 1 0 0 0 22 17"/>""");

    /// <summary>Lucide <c>refresh-cw</c>: reload.</summary>
    public static readonly MarkupString Refresh = Svg(
        """<path d="M3 12a9 9 0 0 1 9-9 9.75 9.75 0 0 1 6.74 2.74L21 8"/><path d="M21 3v5h-5"/><path d="M21 12a9 9 0 0 1-9 9 9.75 9.75 0 0 1-6.74-2.74L3 16"/><path d="M8 16H3v5"/>""");

    /// <summary>Lucide <c>plus</c>: add something (new table in the explorer).</summary>
    public static readonly MarkupString Plus = Svg(
        """<path d="M5 12h14"/><path d="M12 5v14"/>""");

    /// <summary>Lucide <c>house</c>: all connections.</summary>
    public static readonly MarkupString Home = Svg(
        """<path d="M15 21v-8a1 1 0 0 0-1-1h-4a1 1 0 0 0-1 1v8"/><path d="M3 10a2 2 0 0 1 .709-1.528l7-6a2 2 0 0 1 2.582 0l7 6A2 2 0 0 1 21 10v9a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z"/>""");

    /// <summary>Lucide <c>sliders-horizontal</c>: the settings.</summary>
    public static readonly MarkupString Settings = Svg(
        """<path d="M10 5H3"/><path d="M12 19H3"/><path d="M14 3v4"/><path d="M16 17v4"/><path d="M21 12h-9"/><path d="M21 19h-5"/><path d="M21 5h-7"/><path d="M8 10v4"/><path d="M8 12H3"/>""");

    /// <summary>Lucide <c>code</c>: the LINQ console, show the SQL.</summary>
    public static readonly MarkupString Code = Svg(
        """<path d="m16 18 6-6-6-6"/><path d="m8 6-6 6 6 6"/>""");

    /// <summary>Lucide <c>database</c>: the SQL editor (WP-17).</summary>
    public static readonly MarkupString Database = Svg(
        """<ellipse cx="12" cy="5" rx="9" ry="3"/><path d="M3 5V19A9 3 0 0 0 21 19V5"/><path d="M3 12A9 3 0 0 0 21 12"/>""");

    /// <summary>Lucide <c>file-diff</c>: the schema comparison (WP-20), whose result is a DDL script.</summary>
    public static readonly MarkupString SchemaCompare = Svg(
        """<path d="M6 22a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h8a2.4 2.4 0 0 1 1.704.706l3.588 3.588A2.4 2.4 0 0 1 20 8v12a2 2 0 0 1-2 2z"/><path d="M9 10h6"/><path d="M12 13V7"/><path d="M9 17h6"/>""");

    /// <summary>Lucide <c>git-compare</c>: compare the selected rows (WP-61).</summary>
    public static readonly MarkupString CompareRows = Svg(
        """<circle cx="18" cy="18" r="3"/><circle cx="6" cy="6" r="3"/><path d="M13 6h3a2 2 0 0 1 2 2v7"/><path d="M11 18H8a2 2 0 0 1-2-2V9"/>""");

    /// <summary>Lucide <c>activity</c>: the statements of a running app (WP-33).</summary>
    public static readonly MarkupString Pulse = Svg(
        """<path d="M22 12h-2.48a2 2 0 0 0-1.93 1.46l-2.35 8.36a.25.25 0 0 1-.48 0L9.24 2.18a.25.25 0 0 0-.48 0l-2.35 8.36A2 2 0 0 1 4.49 12H2"/>""");

    /// <summary>Lucide <c>pin</c>: a pinned tab (WP-34), a pinned column.</summary>
    public static readonly MarkupString Pin = Svg(
        """<path d="M12 17v5"/><path d="M9 10.76a2 2 0 0 1-1.11 1.79l-1.78.9A2 2 0 0 0 5 15.24V16a1 1 0 0 0 1 1h12a1 1 0 0 0 1-1v-.76a2 2 0 0 0-1.11-1.79l-1.78-.9A2 2 0 0 1 15 10.76V7a1 1 0 0 1 1-1 2 2 0 0 0 0-4H8a2 2 0 0 0 0 4 1 1 0 0 1 1 1z"/>""");

    /// <summary>Lucide <c>workflow</c>: a record diagram (WP-60).</summary>
    public static readonly MarkupString Records = Svg(
        """<rect width="8" height="8" x="3" y="3" rx="2"/><path d="M7 11v4a2 2 0 0 0 2 2h4"/><rect width="8" height="8" x="13" y="13" rx="2"/>""");

    /// <summary>Lucide <c>braces</c>: copy a value as C# (WP-61, quick bar of the cell menu).</summary>
    public static readonly MarkupString Braces = Svg(
        """<path d="M8 3H7a2 2 0 0 0-2 2v5a2 2 0 0 1-2 2 2 2 0 0 1 2 2v5c0 1.1.9 2 2 2h1"/><path d="M16 21h1a2 2 0 0 0 2-2v-5c0-1.1.9-2 2-2a2 2 0 0 1-2-2V5a2 2 0 0 0-2-2h-1"/>""");

    /// <summary>Lucide <c>file-text</c>: open the whole content of a LOB (WP-61).</summary>
    public static readonly MarkupString Document = Svg(
        """<path d="M6 22a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h8a2.4 2.4 0 0 1 1.704.706l3.588 3.588A2.4 2.4 0 0 1 20 8v12a2 2 0 0 1-2 2z"/><path d="M14 2v5a1 1 0 0 0 1 1h5"/><path d="M10 9H8"/><path d="M16 13H8"/><path d="M16 17H8"/>""");

    /// <summary>Lucide <c>notebook-text</c>: the row as a form (WP-61).</summary>
    public static readonly MarkupString Form = Svg(
        """<path d="M2 6h4"/><path d="M2 10h4"/><path d="M2 14h4"/><path d="M2 18h4"/><rect width="16" height="20" x="4" y="2" rx="2"/><path d="M9.5 8h5"/><path d="M9.5 12H16"/><path d="M9.5 16H14"/>""");

    /// <summary>Lucide <c>arrow-right-left</c>: copy rows into another connection (WP-61).</summary>
    public static readonly MarkupString Transfer = Svg(
        """<path d="m16 3 4 4-4 4"/><path d="M20 7H4"/><path d="m8 21-4-4 4-4"/><path d="M4 17h16"/>""");

    /// <summary>Lucide <c>link</c>: copy a link to the selected rows (WP-61).</summary>
    public static readonly MarkupString Link = Svg(
        """<path d="M10 13a5 5 0 0 0 7.54.54l3-3a5 5 0 0 0-7.07-7.07l-1.72 1.71"/><path d="M14 11a5 5 0 0 0-7.54-.54l-3 3a5 5 0 0 0 7.07 7.07l1.71-1.71"/>""");

    /// <summary>Lucide <c>bookmark</c>: copy a link to the view with its filters (WP-61).</summary>
    public static readonly MarkupString LinkView = Svg(
        """<path d="M17 3a2 2 0 0 1 2 2v15a1 1 0 0 1-1.496.868l-4.512-2.578a2 2 0 0 0-1.984 0l-4.512 2.578A1 1 0 0 1 5 20V5a2 2 0 0 1 2-2z"/>""");

    /// <summary>Lucide <c>download</c>: save as a file (WP-61, export matrix).</summary>
    public static readonly MarkupString Download = Svg(
        """<path d="M12 15V3"/><path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4"/><path d="m7 10 5 5 5-5"/>""");

    /// <summary>
    /// Lucide <c>columns-3</c> with its middle column filled: select the whole column (WP-61). The fill is ours; without it
    /// the icon reads as a layout switch.
    /// </summary>
    public static readonly MarkupString SelectColumn = Svg(
        """<rect width="6" height="18" x="9" y="3" fill="currentColor" stroke="none" opacity=".35"/><rect width="18" height="18" x="3" y="3" rx="2"/><path d="M9 3v18"/><path d="M15 3v18"/>""");

    /// <summary>Lucide <c>star</c>: the table's display column (WP-61).</summary>
    public static readonly MarkupString Star = Svg(
        """<path d="M11.525 2.295a.53.53 0 0 1 .95 0l2.31 4.679a2.123 2.123 0 0 0 1.595 1.16l5.166.756a.53.53 0 0 1 .294.904l-3.736 3.638a2.123 2.123 0 0 0-.611 1.878l.882 5.14a.53.53 0 0 1-.771.56l-4.618-2.428a2.122 2.122 0 0 0-1.973 0L6.396 21.01a.53.53 0 0 1-.77-.56l.881-5.139a2.122 2.122 0 0 0-.611-1.879L2.16 9.795a.53.53 0 0 1 .294-.906l5.165-.755a2.122 2.122 0 0 0 1.597-1.16z"/>""");

    /// <summary>Lucide <c>lock-open</c>: a workspace unlocked for writing (WP-10).</summary>
    public static readonly MarkupString Unlocked = Svg(
        """<rect width="18" height="11" x="3" y="11" rx="2" ry="2"/><path d="M7 11V7a5 5 0 0 1 9.9-1"/>""");

    public static MarkupString For(TableKind kind) => kind switch
    {
        TableKind.View => View,
        TableKind.MaterializedView => MaterializedView,
        _ => Table,
    };

    /// <summary>Lucide <c>package</c>: a PL/SQL package (WP-28).</summary>
    public static readonly MarkupString Package = Svg(
        """<path d="M11 21.73a2 2 0 0 0 2 0l7-4A2 2 0 0 0 21 16V8a2 2 0 0 0-1-1.73l-7-4a2 2 0 0 0-2 0l-7 4A2 2 0 0 0 3 8v8a2 2 0 0 0 1 1.73z"/><path d="M12 22V12"/><polyline points="3.29 7 12 12 20.71 7"/><path d="m7.5 4.27 9 5.15"/>""");

    /// <summary>Lucide <c>play</c>: a stored procedure.</summary>
    public static readonly MarkupString Procedure = Svg(
        """<path d="M5 5a2 2 0 0 1 3.008-1.728l11.997 6.998a2 2 0 0 1 .003 3.458l-12 7A2 2 0 0 1 5 19z"/>""");

    /// <summary>Lucide <c>square-function</c>: a stored function.</summary>
    public static readonly MarkupString Function = Svg(
        """<rect width="18" height="18" x="3" y="3" rx="2" ry="2"/><path d="M9 17c2 0 2.8-1 2.8-2.8V10c0-2 1-3.3 3.2-3"/><path d="M9 11.2h5.7"/>""");

    /// <summary>Lucide <c>zap</c>: a trigger.</summary>
    public static readonly MarkupString Trigger = Svg(
        """<path d="M15.914 4a1.5 1.5 0 00-2.474-1.561l-9 9A1.5 1.5 0 005.5 14h4.002a.5.5 0 01.471.666L8.086 20a1.5 1.5 0 002.475 1.56l9-9A1.5 1.5 0 0018.5 10h-3.997a.5.5 0 01-.472-.667z"/>""");

    public static MarkupString For(PlSqlKind kind) => kind switch
    {
        PlSqlKind.Package => Package,
        PlSqlKind.Procedure => Procedure,
        PlSqlKind.Function => Function,
        _ => Trigger,
    };

    // Lucide's own frame: 24 units, stroke 2 – drawn at 14 px a line is 1.17 px, as thick as the earlier hand-drawn icons.
    private static MarkupString Svg(string paths) =>
        new($"""<svg class="icon" width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">{paths}</svg>""");
}
