using FerretSharp.Core.Connections;
using FerretSharp.Core.Links;
using FerretSharp.Core.Resources;
using FerretSharp.UI.Resources;

namespace FerretSharp.UI.State;

/// <summary>"Copy link" in the tab and grid menus (WP-37): the link to the clipboard and a note on what it holds.</summary>
public static class LinkCopy
{
    /// <summary>
    /// The note after copying. On Prod a link may hold values (decision of the user, 2026-10-14): the key of selected
    /// rows always, without a note; other filter values with a note naming them, so nothing leaves unnoticed.
    /// </summary>
    /// <param name="rowsOnly">A link to selected rows: its only values are their key.</param>
    public static (string Text, IReadOnlyList<string> Values) Notice(ConnectionProfile profile, TableLink link, bool rowsOnly)
    {
        var withValues = link.Filters.Where(f => f.Values.Count > 0).ToList();
        return profile.MasksValues && !rowsOnly && withValues.Count > 0
            ? (TextFormat.Format(ShellText.Link_CopiedWithProdValues, profile.Name), FilterPresetText.FilterLines(withValues).ToList())
            : (ShellText.Link_Copied, []);
    }

    public static async Task CopyAsync(ShellState shell, DomModule dom, ConnectionProfile profile, TableLink link, bool rowsOnly = false)
    {
        // The https form (ADR 0026, addendum): chats and tickets make only https clickable; the page opens ferretsharp://.
        if (await dom.CopyTextAsync(shell, link.ToWebUri()))
        {
            var (text, values) = Notice(profile, link, rowsOnly);
            shell.Notify(text, values);
        }
    }
}
