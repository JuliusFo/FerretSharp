using System.Text.Json;
using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Connections;
using FerretSharp.Core.Data;
using FerretSharp.Core.Query;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Schema;
using FerretSharp.UI.Resources;

namespace FerretSharp.UI.State;

/// <summary>What reloading the schema found (WP-22: after DDL, and "Schema neu laden").</summary>
/// <param name="Error">The schema could not be read; nothing else was done.</param>
/// <param name="NewColumnsWithoutProperty">
/// Columns of mapped tables that the C# model has no property for and that it did not miss before (<c>KUNDEN.FAX</c>).
/// </param>
/// <param name="StaleTabs">Table tabs whose structure changed but that keep the old one: they hold uncommitted changes.</param>
/// <param name="ModelError">The C# model could not be matched again (it keeps the previous match).</param>
public sealed record SchemaRefreshResult(
    DatabaseException? Error,
    IReadOnlyList<string> NewColumnsWithoutProperty,
    IReadOnlyList<string> StaleTabs,
    DatabaseException? ModelError = null)
{
    public static readonly SchemaRefreshResult None = new(null, [], []);

    /// <summary>What the user should know, as sentences (toast warnings).</summary>
    public IReadOnlyList<string> Warnings
    {
        get
        {
            var warnings = new List<string>();
            if (NewColumnsWithoutProperty.Count > 0)
            {
                warnings.Add(TextFormat.Plural(NewColumnsWithoutProperty.Count, ShellText.Schema_NewColumnsWithoutPropertyOne,
                    ShellText.Schema_NewColumnsWithoutPropertyOther, string.Join(", ", NewColumnsWithoutProperty.Take(MaxNamed))
                                                                     + (NewColumnsWithoutProperty.Count > MaxNamed ? ", …" : "")));
            }

            warnings.AddRange(StaleTabs.Select(tab => TextFormat.Format(ShellText.Schema_StaleTab, tab)));
            if (ModelError is { } error)
            {
                warnings.Add(TextFormat.Format(ShellText.Schema_ModelNotMatched, error.Display));
            }

            return warnings;
        }
    }

    /// <summary>Columns named in the warning; more are counted only.</summary>
    private const int MaxNamed = 5;
}

/// <summary>Another workspace with an open write transaction (WP-22): DDL on <paramref name="Tables"/> is refused while it lasts.</summary>
public sealed record OtherWriter(string Workspace, IReadOnlyList<string> Tables);

/// <summary>A table DDL found locked by another session (WP-22).</summary>
/// <param name="Holders">The sessions holding locks on it; null if unknown (no rights on the V$ views).</param>
public sealed record TableLock(TableRef Table, IReadOnlyList<LockHolder>? Holders);

// Reloading the schema of a connection and everything built on it (WP-22, a file of its own).
public sealed partial class WorkspaceLifecycle
{
    /// <summary>
    /// Reloads the connection's schema (explorer, completion) and what depends on it: tabs of objects that are gone close,
    /// table tabs whose structure changed build their views again, and the C# model is matched again – without the model
    /// host. Tabs with uncommitted changes keep their structure: their changes address columns by position.
    /// </summary>
    public async Task<SchemaRefreshResult> RefreshSchemaAsync(ConnectionScope scope)
    {
        if (scope.Active.Schema is not { } schema)
        {
            return SchemaRefreshResult.None;
        }

        var missedBefore = ColumnsWithoutProperty(scope.Models.Mapping);
        var refreshed = await shell.RunDbAsync(logger, scope.Active, () => schema.RefreshAsync(CancellationToken.None));
        if (!refreshed.Succeeded)
        {
            return refreshed.Error is { } error ? new SchemaRefreshResult(error, [], []) : SchemaRefreshResult.None;
        }

        shell.RemoveVanishedTabs(scope.Id, schema);
        var stale = await RebuildChangedTabsAsync(scope, schema);

        var remapped = await shell.RunDbAsync(logger, scope.Active, () => scope.Models.RemapAsync(CancellationToken.None));
        var added = remapped.Value is { } mapping
            ? ColumnsWithoutProperty(mapping).Where(c => !missedBefore.Contains(c)).Select(c => $"{c.Table.Name}.{c.Column}").ToList()
            : [];
        shell.NotifyChanged();
        return new SchemaRefreshResult(null, added, stale, remapped.Error);
    }

    /// <summary>
    /// Table tabs of the connection that loaded their structure compare it with the new one; a changed one counts up its
    /// <see cref="TableTab.StructureVersion"/>, and its view starts over.
    /// </summary>
    /// <returns>The tabs left with the old structure (uncommitted changes).</returns>
    private async Task<IReadOnlyList<string>> RebuildChangedTabsAsync(ConnectionScope scope, SchemaCache schema)
    {
        var stale = new List<string>();
        var tabs = shell.AllWorkspaces.Where(w => w.ConnectionId == scope.Id).SelectMany(w => w.TableTabs).Where(t => t.Details is not null).ToList();
        foreach (var tab in tabs)
        {
            var current = await shell.RunDbAsync(logger, scope.Active, () => schema.GetDetailsAsync(tab.Table, CancellationToken.None));
            if (current.Value is not { } details || SameStructure(details, tab.Details!))
            {
                continue;
            }

            if (tab.Changes is { PendingCount: > 0 } or { FlushedCount: > 0 })
            {
                stale.Add(tab.Table.DisplayName);
                continue;
            }

            tab.Details = details;
            tab.StructureVersion++;
        }

        return stale.Distinct().ToList();
    }

    /// <summary>
    /// Other workspaces of the same connection with an open write transaction, and the tables they wrote to (newest
    /// statements of grid and SQL alike). DDL on one of those tables either fails (ORA-00054) or – ALTER TABLE … ADD – waits
    /// until that transaction ends and cannot be cancelled meanwhile (ADR 0019); the DDL confirmation says so beforehand.
    /// </summary>
    public IReadOnlyList<OtherWriter> OtherWriters(Guid workspaceId)
    {
        var connection = shell.FindWorkspace(workspaceId)?.ConnectionId;
        return shell.AllWorkspaces
            .Where(w => w.ConnectionId == connection && w.WorkspaceId != workspaceId)
            .Select(w => (w.WorkspaceId, Summary: editing.SummaryOf(w)))
            .Where(w => w.Summary.Transaction?.Mode == TransactionMode.ReadWrite)
            .Select(w => new OtherWriter(WorkspaceName(w.WorkspaceId), TablesOf(w.Summary.Actions)))
            .ToList();
    }

    /// <summary>
    /// After DDL failed on a locked table (the schema path did not start ALTER TABLE, or DROP and the like got ORA-00054):
    /// the table the statement changes and who holds locks on it – read on the explorer session, which needs read access
    /// to V$LOCKED_OBJECT and V$SESSION. Null if the failure is no lock problem or the table is not in the schema.
    /// </summary>
    public async Task<TableLock?> LockOfAsync(ConnectionScope scope, string statement, DatabaseException failure)
    {
        if (!(failure.InnerException is TableBusyException || failure.ErrorCode == "ORA-00054")
            || scope.Active.Schema is not { } schema
            || SqlScript.DdlTableOf(statement) is not { } reference
            || SqlCompletion.Resolve(schema, reference) is not { } table)
        {
            return null;
        }

        var holders = await shell.RunDbAsync(logger, scope.Active, () => schema.GetLockHoldersAsync(table.Ref, CancellationToken.None));
        return new TableLock(table.Ref, holders.Value);
    }

    /// <summary>The tables the writes changed – the target of each DML, as the SQL editor reads it.</summary>
    private static IReadOnlyList<string> TablesOf(IEnumerable<WriteAction> actions) =>
        actions.SelectMany(a => a.Statements)
            .Select(s => SqlScript.Analyze(s.Sql).Tables.FirstOrDefault(t => t.Depth == 0)?.Name)
            .OfType<string>()
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();

    /// <summary>Records hold lists, so record equality compares references; the JSON form compares what they say.</summary>
    private static bool SameStructure(TableDetails a, TableDetails b) => JsonSerializer.Serialize(a) == JsonSerializer.Serialize(b);

    private static HashSet<(TableRef Table, string Column)> ColumnsWithoutProperty(ClrModelMapping? mapping) =>
        mapping?.Issues
            .Where(i => i is { Kind: MappingIssueKind.ColumnWithoutProperty, Table: not null, Column: not null })
            .Select(i => (i.Table!, i.Column!))
            .ToHashSet() ?? [];
}
