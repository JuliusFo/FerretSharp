using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Workspaces;

namespace FerretSharp.UI.State;

/// <summary>A tab of a workspace: a table (<see cref="TableTab"/>) or a LINQ console (<see cref="LinqTab"/>).</summary>
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

    /// <param name="workspaceTabs">All tabs of the workspace in order (references between tabs are saved as indexes).</param>
    public abstract TabState ToState(IReadOnlyList<WorkspaceTab> workspaceTabs);
}

/// <summary>A LINQ console tab (WP-13, ADR 0011): C# against the linked project's DbContext, run in the workspace's session.</summary>
public sealed class LinqTab(Guid workspaceId, string title) : WorkspaceTab(workspaceId, "linq-")
{
    public string Title { get; set; } = title;

    public string Code { get; set; } = "";

    /// <summary>Declarations the code uses (<c>var customerId = 4711;</c>), kept apart so a copied query stays as it was.</summary>
    public string Variables { get; set; } = "";

    /// <summary>The last run: diagnostics, suggestions, the captured commands. Not saved.</summary>
    public LinqRunResult? LastRun { get; set; }

    /// <summary>Index into <c>LastRun.Commands</c> of the command shown.</summary>
    public int SelectedCommand { get; set; }

    public override TabState ToState(IReadOnlyList<WorkspaceTab> workspaceTabs) =>
        new(LinqTabState.NoTable, TabMode.Data, [], [], []) { Linq = new LinqTabState(Title, Code, Variables) };

    public static LinqTab Restore(Guid workspaceId, LinqTabState state) =>
        new(workspaceId, state.Title) { Code = state.Code, Variables = state.Variables };
}
