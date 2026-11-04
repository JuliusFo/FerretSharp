using System.Globalization;
using System.Text;
using FerretSharp.Core.Settings;

namespace FerretSharp.UI.State;

/// <summary>
/// Sizes the user dragged that hold for the whole app – the explorer of every connection, the editors of every SQL and LINQ
/// tab – as CSS custom properties on the app root. The stylesheet falls back to its default where one is missing; while
/// the handle is dragged, <c>dom.js dragSize</c> sets the same property, and the shell renders the saved value afterwards.
/// </summary>
public static class PanelSizes
{
    public const string ExplorerWidth = "--explorer-width";
    public const string SqlEditorHeight = "--sql-editor-height";
    public const string LinqEditorHeight = "--linq-editor-height";

    /// <summary>The style of the app root for the sizes in the settings; null if all are the defaults.</summary>
    public static string? Style(AppSettings settings)
    {
        var style = new StringBuilder();
        Append(ExplorerWidth, settings.ExplorerWidth);
        Append(SqlEditorHeight, settings.SqlEditorHeight);
        Append(LinqEditorHeight, settings.LinqEditorHeight);
        return style.Length == 0 ? null : style.ToString().TrimEnd();

        void Append(string variable, int? pixels)
        {
            if (pixels is > 0)
            {
                style.Append(CultureInfo.InvariantCulture, $"{variable}: {pixels}px; ");
            }
        }
    }
}
