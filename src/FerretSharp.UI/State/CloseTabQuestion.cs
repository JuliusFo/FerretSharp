namespace FerretSharp.UI.State;

/// <summary>What closing a tab would lose, so the user is asked first.</summary>
public enum CloseTabLoss
{
    /// <summary>Pending changes of a table tab (not written yet).</summary>
    PendingChanges,

    /// <summary>A value still being typed in a grid cell (only known to the shortcut: clicking ✕ takes the focus and ends the input).</summary>
    CellInput,

    /// <summary>The text of a SQL or LINQ tab.</summary>
    EditorText,
}

/// <summary>A tab waiting for "Discard and close".</summary>
public sealed record CloseTabQuestion(WorkspaceTab Tab, CloseTabLoss Loss)
{
    /// <summary>Why <paramref name="tab"/> must not close without asking; null if nothing would be lost.</summary>
    public static CloseTabQuestion? For(WorkspaceTab tab, bool cellEditing) => tab switch
    {
        TableTab { Changes.PendingCount: > 0 } => new(tab, CloseTabLoss.PendingChanges),
        TableTab when cellEditing => new(tab, CloseTabLoss.CellInput),
        SqlTab sql when !string.IsNullOrWhiteSpace(sql.Text) => new(tab, CloseTabLoss.EditorText),
        LinqTab linq when !string.IsNullOrWhiteSpace(linq.Code) => new(tab, CloseTabLoss.EditorText),
        _ => null,
    };
}
