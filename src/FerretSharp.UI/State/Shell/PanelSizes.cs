using System.Globalization;
using FerretSharp.Core.Settings;

namespace FerretSharp.UI.State;

/// <summary>
/// Dragged sizes as CSS custom properties; the stylesheet falls back to its default where one is missing, and while a
/// handle is dragged <c>dom.js dragSize</c> sets the same property on the same element. The explorer's width holds for the
/// whole app (in the settings, on the app root); the editor heights of the SQL editor and the LINQ console are per tab
/// (decision of the user, in the tab's state, on its view).
/// </summary>
public static class PanelSizes
{
    public const string ExplorerWidth = "--explorer-width";
    public const string SqlEditorHeight = "--sql-editor-height";
    public const string LinqEditorHeight = "--linq-editor-height";

    /// <summary>The style of the app root for the sizes in the settings; null if all are the defaults.</summary>
    public static string? Style(AppSettings settings) => Property(ExplorerWidth, settings.ExplorerWidth);

    /// <summary>The style that carries one dragged size, e.g. a tab's editor height on its view; null for the default.</summary>
    public static string? Property(string variable, int? pixels) =>
        pixels is > 0 ? string.Create(CultureInfo.InvariantCulture, $"{variable}: {pixels}px;") : null;
}
