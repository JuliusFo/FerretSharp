using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Data;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;

namespace FerretSharp.UI.State;

/// <summary>A changed cell of a pending row: column label, the value in the transaction and the new one (display texts, null = NULL).</summary>
public sealed record PendingCell(int Column, string Label, string? Old, string? New);

/// <param name="Key">The row's key as text ("4711", "4711 · 2", a ROWID); null for a new row.</param>
/// <param name="Cells">Changed cells (UPDATE), filled cells (INSERT); none for a deletion.</param>
public sealed record PendingRow(RowChange Change, OperationKind Kind, string? Key, IReadOnlyList<PendingCell> Cells);

public sealed record PendingTab(TableTab Tab, IReadOnlyList<PendingRow> Rows);

/// <summary>
/// What the change overview shows of a workspace (WP-30): its pending changes by tab and the statements of its written
/// actions – from the tabs' changes and the actions, without the database. Values and labels through the presentation
/// layer (C# names, enum members).
/// </summary>
public static class ChangeOverview
{
    /// <summary>The tabs with pending changes, in tab order.</summary>
    public static IReadOnlyList<PendingTab> Pending(WorkspaceTabs workspace, Func<TableDetails, TablePresentation> presentation) =>
        workspace.TableTabs
            .Where(t => t.Changes?.PendingCount > 0)
            .Select(t => new PendingTab(t, Rows(t.Changes!, presentation(t.Changes!.Table))))
            .ToList();

    /// <summary>The pending rows of a tab: new rows, changed rows, rows marked for deletion.</summary>
    public static IReadOnlyList<PendingRow> Rows(ChangeTracker tracker, TablePresentation presentation) =>
        tracker.Changes
            .Where(c => c.HasPending)
            .Select(c => Row(c, presentation))
            .OrderBy(r => r.Kind)
            .ToList();

    private static PendingRow Row(RowChange change, TablePresentation presentation)
    {
        var kind = change.PendingKind!.Value;
        var cells = kind == OperationKind.Delete
            ? []
            : change.PendingColumns
                .Order()
                .Where(c => kind != OperationKind.Insert || change.ValueOf(c) is not null)
                .Select(c => new PendingCell(
                    c,
                    presentation.LabelOf(c).Name,
                    kind == OperationKind.Insert ? null : presentation.Present(c, change.ExpectedOf(c)).Text,
                    presentation.Present(c, change.ValueOf(c)).Text))
                .ToList();
        return new PendingRow(change, kind, KeyText(presentation.Details, change.Key), cells);
    }

    /// <summary>A row key as the user reads it: the primary key values, or the ROWID; null for a new row.</summary>
    public static string? KeyText(TableDetails table, RowKey key) => key switch
    {
        RowKey.PrimaryKey pk => string.Join(" · ", pk.Values.Select((value, i) =>
            table.IndexOf(table.PrimaryKey[i]) is >= 0 and var column ? CellFormatter.Format(table.Columns[column], value) ?? "NULL" : "?")),
        RowKey.RowId rowId => rowId.Value,
        _ => null,
    };

    /// <summary>
    /// The statements of a written action with their bind values, as the user may run them again – shown in the UI only,
    /// also on Prod (CLAUDE.md: never in a log). The ROWID output of an INSERT is left out.
    /// </summary>
    public static string Sql(WriteAction action) =>
        string.Join(Environment.NewLine + Environment.NewLine, action.Statements.Select(s =>
            BindValues.Describe(new QuerySpec(s.Sql, s.Parameters.Where(p => !p.Output).ToList()), mask: false) + ";"));
}
