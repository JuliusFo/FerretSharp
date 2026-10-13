namespace FerretSharp.UI.State;

/// <summary>What closing a tab would lose, so the user is asked first.</summary>
public enum CloseTabLoss
{
    /// <summary>Pending changes of a table tab (not written yet).</summary>
    PendingChanges,

    /// <summary>
    /// A value typed but not confirmed yet: in a grid cell being edited (asked from shortcuts.js by the shortcut and the
    /// tab bar) or in a field of the form beside the grid. The dialog takes the focus, which usually turns the value into
    /// a pending change – the dialog then counts pending changes instead.
    /// </summary>
    TypedValue,

    /// <summary>The draft of the table designer (WP-23): a table tab in edit mode, a new table.</summary>
    TableDraft,

    /// <summary>The text of a SQL or LINQ tab.</summary>
    EditorText,
}

/// <summary>A tab waiting for "Discard and close".</summary>
public sealed record CloseTabQuestion(WorkspaceTab Tab, CloseTabLoss Loss)
{
    /// <summary>Why <paramref name="tab"/> must not close without asking; null if nothing would be lost.</summary>
    public static CloseTabQuestion? For(WorkspaceTab tab, bool typedValue) => tab switch
    {
        TableTab { Changes.PendingCount: > 0 } => new(tab, CloseTabLoss.PendingChanges),
        TableTab { Designer.Draft.HasChanges: true } or NewTableTab { Designer.HasChanges: true } => new(tab, CloseTabLoss.TableDraft),
        TableTab when typedValue => new(tab, CloseTabLoss.TypedValue),
        SqlTab sql when !string.IsNullOrWhiteSpace(sql.Text) => new(tab, CloseTabLoss.EditorText),
        LinqTab linq when !string.IsNullOrWhiteSpace(linq.Code) => new(tab, CloseTabLoss.EditorText),
        _ => null,
    };
}

/// <summary>
/// Which tabs a "close" from the tab's right-click menu or a shortcut takes, seen from one tab. Pinned tabs (WP-34) close only
/// by themselves (<see cref="This"/>).
/// </summary>
public enum TabCloseScope
{
    /// <summary>The tab itself.</summary>
    This,

    /// <summary>Every other tab of the workspace that is not pinned.</summary>
    Others,

    /// <summary>The tabs to the right of it that are not pinned.</summary>
    ToTheRight,

    /// <summary>Every tab of the workspace that is not pinned.</summary>
    All,
}

/// <summary>
/// Several tabs waiting to close, some of which would lose something (<see cref="Losing"/>): discard and close all, close
/// only the others (<see cref="Others"/>), or cancel.
/// </summary>
/// <param name="Target">The tab the menu or shortcut was used on; stays active if it stays open.</param>
public sealed record CloseTabsQuestion(WorkspaceTab Target, IReadOnlyList<WorkspaceTab> Tabs, IReadOnlyList<CloseTabQuestion> Losing)
{
    /// <summary>The tabs that close without losing anything.</summary>
    public IReadOnlyList<WorkspaceTab> Others => [.. Tabs.Where(t => Losing.All(q => q.Tab != t))];

    /// <summary>The tabs of <paramref name="tabs"/> (in order) that <paramref name="scope"/> takes, seen from <paramref name="target"/>.</summary>
    public static IReadOnlyList<WorkspaceTab> Select(IReadOnlyList<WorkspaceTab> tabs, WorkspaceTab target, TabCloseScope scope)
    {
        if (!tabs.Contains(target))
        {
            return [];
        }

        return scope switch
        {
            TabCloseScope.This => [target],
            TabCloseScope.Others => [.. tabs.Where(t => t != target && !t.Pinned)],
            TabCloseScope.ToTheRight => [.. tabs.SkipWhile(t => t != target).Skip(1).Where(t => !t.Pinned)],
            _ => [.. tabs.Where(t => !t.Pinned)],
        };
    }
}
