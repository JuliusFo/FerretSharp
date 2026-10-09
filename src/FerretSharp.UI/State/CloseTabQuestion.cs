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
        TableTab when typedValue => new(tab, CloseTabLoss.TypedValue),
        SqlTab sql when !string.IsNullOrWhiteSpace(sql.Text) => new(tab, CloseTabLoss.EditorText),
        LinqTab linq when !string.IsNullOrWhiteSpace(linq.Code) => new(tab, CloseTabLoss.EditorText),
        _ => null,
    };
}
