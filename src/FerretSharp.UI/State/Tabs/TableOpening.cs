using FerretSharp.Core.Settings;
using FerretSharp.Core.Workspaces;

namespace FerretSharp.UI.State;

/// <summary>
/// How a newly opened table tab starts when nothing asks for a view (settings "View when opening" and "Load rows"): the
/// explorer, the command palette, the diagrams. An FK jump, a link, a duplicate and a restored tab bring their own.
/// </summary>
public static class TableOpening
{
    /// <summary>The setting's view; one this version does not know (a number written by hand) is the data.</summary>
    public static TabMode View(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return Enum.IsDefined(settings.OpenView) ? settings.OpenView : TabMode.Data;
    }

    /// <summary>The rows of a new tab wait until the user asks for them.</summary>
    public static bool DefersRows(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.LoadRows == RowLoadMode.OnRequest;
    }

    /// <summary>
    /// The view a tab opened on the setting's view shows: "Usage" needs a linked C# project (without one there is no code to
    /// search and the view switch has no such view), else the data.
    /// </summary>
    public static TabMode Shown(TabMode view, bool codeLinked) => view == TabMode.Usage && !codeLinked ? TabMode.Data : view;
}
