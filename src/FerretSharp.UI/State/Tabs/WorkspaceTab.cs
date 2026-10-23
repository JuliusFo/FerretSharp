using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Design;
using FerretSharp.Core.Schema;
using FerretSharp.Core.Workspaces;
using FerretSharp.UI.Resources;

namespace FerretSharp.UI.State;

/// <summary>
/// A tab of a workspace: a table (<see cref="TableTab"/>), a LINQ console (<see cref="LinqTab"/>), a SQL editor
/// (<see cref="SqlTab"/>), a PL/SQL unit (<see cref="PlSqlTab"/>) or a record diagram (<see cref="RecordTab"/>).
/// </summary>
public abstract class WorkspaceTab(Guid workspaceId, string idPrefix)
{
    /// <summary>Unique element id (the grid's host element of a table tab).</summary>
    public string Id { get; } = idPrefix + Guid.NewGuid().ToString("N");

    /// <summary>The workspace whose session runs this tab's queries.</summary>
    public Guid WorkspaceId { get; } = workspaceId;

    /// <summary>
    /// Set once the tab has been shown. Restored tabs are mounted (and query) only when first shown, so reopening
    /// a workspace with many tabs does not fire all their queries at once.
    /// </summary>
    public bool Visited { get; set; }

    /// <summary>
    /// Pinned by the user (WP-34): stands at the left with the other pinned tabs, and closing other tabs, the tabs to the
    /// right or all tabs leaves it open. Set through <see cref="ShellState.SetPinned"/>, which keeps the order.
    /// </summary>
    public bool Pinned { get; set; }

    /// <param name="workspaceTabs">All tabs of the workspace in order (references between tabs are saved as indexes).</param>
    public abstract TabState ToState(IReadOnlyList<WorkspaceTab> workspaceTabs);

    /// <summary>What is saved with the workspace: the state of the tab's kind and what every tab has (the pin).</summary>
    public TabState Save(IReadOnlyList<WorkspaceTab> workspaceTabs) => ToState(workspaceTabs) with { Pinned = Pinned };

    /// <summary>The tab is saved with the workspace (not the draft of a new table, WP-23).</summary>
    public virtual bool IsSaved => true;

    /// <summary>
    /// How the tab is named in the tab bar, the dialogs and the change overview (R5b: written three times; the overview
    /// named PL/SQL tabs with nothing).
    /// </summary>
    public string Caption => this switch
    {
        TableTab table => table.Table.DisplayName,
        NewTableTab draft => draft.Table.Name.Length > 0 ? draft.Table.Name : ShellText.TabBar_NewTable,
        PlSqlTab plSql => plSql.Unit.DisplayName,
        RecordTab records => records.Title,
        ITitledTab titled => titled.Title,
        _ => "",
    };
}

/// <summary>A tab with a title of its own ("SQL 1", "Kunden suchen") that the user can rename – unlike a table tab, named after its table.</summary>
public interface ITitledTab
{
    string Title { get; set; }
}

/// <summary>
/// A tab with the form beside its grid (WP-21; the result of the SQL editor and the LINQ console since WP-56), saved with
/// the tab as <see cref="TabState.Form"/>.
/// </summary>
public interface IFormTab
{
    /// <summary>The form beside the grid is open.</summary>
    bool FormOpen { get; set; }

    /// <summary>Width of the form in CSS pixels; null for the default.</summary>
    int? FormWidth { get; set; }

    /// <summary>"Leere ausblenden" in the form, per tab (decision of the user).</summary>
    bool FormHideEmpty { get; set; }
}

public static class FormTabs
{
    /// <summary>What is saved of the form; null if it was never used in the tab.</summary>
    public static FormTabState? Save(IFormTab tab) =>
        tab.FormOpen || tab.FormWidth is not null || tab.FormHideEmpty ? new FormTabState(tab.FormOpen, tab.FormWidth, tab.FormHideEmpty) : null;

    public static T WithForm<T>(this T tab, FormTabState? state) where T : IFormTab
    {
        tab.FormOpen = state?.Open ?? false;
        tab.FormWidth = state?.Width;
        tab.FormHideEmpty = state?.HideEmpty ?? false;
        return tab;
    }
}

/// <summary>A LINQ console tab (WP-13, ADR 0011): C# against the linked project's DbContext, run in the workspace's session.</summary>
public sealed class LinqTab(Guid workspaceId, string title) : WorkspaceTab(workspaceId, "linq-"), ITitledTab, IFormTab
{
    public string Title { get; set; } = title;

    public string Code { get; set; } = "";

    /// <summary>Declarations the code uses (<c>var customerId = 4711;</c>), kept apart so a copied query stays as it was.</summary>
    public string Variables { get; set; } = "";

    /// <summary>The last run: diagnostics, suggestions, the captured commands. Not saved.</summary>
    public LinqRunResult? LastRun { get; set; }

    /// <summary>Index into <c>LastRun.Commands</c> of the command shown.</summary>
    public int SelectedCommand { get; set; }

    public bool FormOpen { get; set; }

    public int? FormWidth { get; set; }

    public bool FormHideEmpty { get; set; }

    public override TabState ToState(IReadOnlyList<WorkspaceTab> workspaceTabs) =>
        TabState.OfLinq(new LinqTabState(Title, Code, Variables)) with { Form = FormTabs.Save(this) };

    public static LinqTab Restore(Guid workspaceId, LinqTabState state, FormTabState? form = null) =>
        new LinqTab(workspaceId, state.Title) { Code = state.Code, Variables = state.Variables }.WithForm(form);
}

/// <summary>A SQL editor tab (WP-17, ADR 0014): a script whose statement at the cursor runs in the workspace's session.</summary>
public sealed class SqlTab(Guid workspaceId, string title) : WorkspaceTab(workspaceId, "sql-"), ITitledTab, IFormTab
{
    public string Title { get; set; } = title;

    public string Text { get; set; } = "";

    /// <summary>Bind variables in order of use, with type and value; unused ones are kept at the end.</summary>
    public IReadOnlyList<Core.Query.SqlVariable> Variables { get; set; } = [];

    /// <summary>The statements of the last run – one for Ctrl+Enter, several for a script (Alt+X). Not saved.</summary>
    public IReadOnlyList<SqlRun> Runs { get; set; } = [];

    /// <summary>Index into <see cref="Runs"/> of the result shown.</summary>
    public int SelectedRun { get; set; }

    public SqlRun? ShownRun => Runs.Count == 0 ? null : Runs[Math.Clamp(SelectedRun, 0, Runs.Count - 1)];

    public bool FormOpen { get; set; }

    public int? FormWidth { get; set; }

    public bool FormHideEmpty { get; set; }

    public override TabState ToState(IReadOnlyList<WorkspaceTab> workspaceTabs) =>
        TabState.OfSql(new SqlTabState(Title, Text, Variables)) with { Form = FormTabs.Save(this) };

    public static SqlTab Restore(Guid workspaceId, SqlTabState state, FormTabState? form = null) =>
        new SqlTab(workspaceId, state.Title) { Text = state.Text, Variables = state.Variables }.WithForm(form);
}

/// <summary>
/// A PL/SQL tab (WP-28, view only): a package, procedure, function or trigger with its source, parameters, compile errors
/// and dependencies. Read on the explorer session like the detail views of a table.
/// </summary>
public sealed class PlSqlTab(Guid workspaceId, Core.Schema.PlSqlObjectSummary unit) : WorkspaceTab(workspaceId, "plsql-")
{
    public Core.Schema.PlSqlObjectSummary Unit { get; } = unit;

    public PlSqlView View { get; set; } = PlSqlView.Source;

    /// <summary>A line to show once the source of the part is there (a compile error, a search hit); taken by the source view.</summary>
    public SourcePosition? Reveal { get; set; }

    /// <summary>The views this kind of unit has, in toolbar order: packages have a body, triggers no parameters.</summary>
    public IReadOnlyList<PlSqlView> Views => Unit.Kind switch
    {
        Core.Schema.PlSqlKind.Package => [PlSqlView.Source, PlSqlView.Body, PlSqlView.Parameters, PlSqlView.Errors, PlSqlView.Dependencies],
        Core.Schema.PlSqlKind.Trigger => [PlSqlView.Source, PlSqlView.Errors, PlSqlView.Dependencies],
        _ => [PlSqlView.Source, PlSqlView.Parameters, PlSqlView.Errors, PlSqlView.Dependencies],
    };

    /// <summary>Shows a part's source at a line: switches to that view and leaves the position for the source view.</summary>
    public void ShowSource(Core.Schema.PlSqlPart part, int line, int column = 1)
    {
        View = SourceView(part);
        Reveal = new SourcePosition(part, line, column);
    }

    public static PlSqlView SourceView(Core.Schema.PlSqlPart part) => part == Core.Schema.PlSqlPart.Body ? PlSqlView.Body : PlSqlView.Source;

    public override TabState ToState(IReadOnlyList<WorkspaceTab> workspaceTabs) =>
        TabState.OfPlSql(new PlSqlTabState(Unit.Owner, Unit.Name, Unit.Kind, View));

    /// <summary>The tab of a saved state; a view the kind does not have falls back to the source.</summary>
    public static PlSqlTab Restore(Guid workspaceId, Core.Schema.PlSqlObjectSummary unit, PlSqlTabState state)
    {
        var tab = new PlSqlTab(workspaceId, unit);
        tab.View = tab.Views.Contains(state.View) ? state.View : PlSqlView.Source;
        return tab;
    }
}

/// <summary>
/// A record diagram (WP-60): the rows connected to rows of a table, read on the workspace's session as the user expands
/// them. Saved with the start rows' keys and what was expanded; the rows are read again when the tab is shown.
/// </summary>
public sealed class RecordTab(Guid workspaceId, TableSummary table, RecordDiagramState diagram) : WorkspaceTab(workspaceId, "records-")
{
    /// <summary>The table of the start rows.</summary>
    public TableSummary Table { get; } = table;

    public RecordDiagramState Diagram { get; } = diagram;

    /// <summary>"AUFTRAG 4711" for one start row, "AUFTRAG ×3" for several; ROWIDs say nothing to the user.</summary>
    public string Title => Diagram.Keys switch
    {
        [var key] when !Diagram.RowIds => $"{Table.DisplayName} {string.Join(" · ", key)}",
        { Count: > 1 } keys => $"{Table.DisplayName} ×{keys.Count}",
        _ => Table.DisplayName,
    };

    public override TabState ToState(IReadOnlyList<WorkspaceTab> workspaceTabs) =>
        TabState.OfRecords(new RecordTabState(Table.Owner, Table.Name, Diagram.RowIds, Diagram.Keys, Diagram.Steps));

    /// <summary>
    /// The tab of a saved state. No steps is a new diagram, expanded on both sides: a tab saved before its first load ended
    /// (closed meanwhile, cancelled, the connection lost) has none (review) – and start rows without any relationship
    /// expand to nothing either way.
    /// </summary>
    public static RecordTab Restore(Guid workspaceId, TableSummary table, RecordTabState state) =>
        new(workspaceId, table, new RecordDiagramState(table.Ref, state.RowIds, state.Keys, state.Steps.Count == 0 ? null : state.Steps));
}

/// <summary>
/// A table to create in the designer (WP-23, stage 3): name, comment and the draft of its columns and constraints. Not
/// saved with the workspace – a restart drops the draft; once the table exists it opens as a table tab.
/// </summary>
public sealed class NewTableTab : WorkspaceTab
{
    public NewTableTab(Guid workspaceId, string owner) : base(workspaceId, "newtable-")
    {
        Owner = owner;
        Designer = new ColumnDesigner(new TableDetails(new TableSummary(owner, "", TableKind.Table), [], [], [], false), []);
    }

    /// <summary>The schema the table is created in (the connection's).</summary>
    public string Owner { get; }

    /// <summary>The name as typed: without quotes upper-cased (<see cref="Core.Oracle.OracleIdentifier.Normalize"/>).</summary>
    public string Name { get; set; } = "";

    public string Comment { get; set; } = "";

    public ColumnDesigner Designer { get; }

    /// <summary>How the last run failed, while the table does not exist yet.</summary>
    public DdlRunResult? Failure { get; set; }

    /// <summary>The table to create, as the designer needs it.</summary>
    public NewTable Table => new(Owner, Core.Oracle.OracleIdentifier.Normalize(Name), Comment.Length > 0 ? Comment : null);

    public override bool IsSaved => false;

    public override TabState ToState(IReadOnlyList<WorkspaceTab> workspaceTabs) =>
        throw new NotSupportedException("The draft of a new table is not saved.");
}

/// <param name="Line">From 1, as in <c>ALL_SOURCE.LINE</c>.</param>
/// <param name="Column">From 1.</param>
public sealed record SourcePosition(Core.Schema.PlSqlPart Part, int Line, int Column);

/// <summary>A statement the SQL editor ran and what came of it.</summary>
/// <param name="Number">Position in the script (1, 2 …); 0 for a single statement (Ctrl+Enter).</param>
/// <param name="Query">The bound statement: read for a query (more pages later by the result grid), executed once for DML.</param>
/// <param name="FirstPage">A query's first page, read when the statement ran – a later UPDATE of the script does not change it.</param>
/// <param name="Changed">DML: rows changed; null for queries and failures.</param>
/// <param name="Transaction">DML: start of the workspace transaction it ran in – to tell whether that one is still open.</param>
/// <param name="Error">Why it failed (or "Abgebrochen."); the script stopped here.</param>
/// <param name="Action">DML: its write in the transaction – undo (↶) may take it back (<c>WorkspaceEditing.FateOf</c>).</param>
/// <param name="Elapsed">From start to result (a query: its first page) as the user waited for it; null while it runs.</param>
/// <param name="Cancelled">Stopped by "Abbrechen" (or the session was given up); <see cref="Error"/> says so.</param>
/// <param name="SchemaChanged">DDL that ran (WP-22): committed at once, nothing to undo.</param>
/// <param name="Lock">DDL that met a table another session holds (WP-22): which table, and who holds it.</param>
public sealed record SqlRun(
    int Number,
    Core.Query.SqlStatement Statement,
    Core.Query.SqlStatementInfo Info,
    Core.Query.QuerySpec Query,
    DateTimeOffset At,
    Core.Data.SqlPage? FirstPage = null,
    int? Changed = null,
    DateTimeOffset? Transaction = null,
    Core.Connections.DatabaseException? Error = null,
    Guid? Action = null,
    TimeSpan? Elapsed = null,
    bool Cancelled = false,
    bool SchemaChanged = false,
    TableLock? Lock = null);
