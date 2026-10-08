using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Data;
using FerretSharp.Core.Oracle;
using FerretSharp.Core.Schema;
using FerretSharp.Core.Workspaces;

namespace FerretSharp.Core.Forms;

/// <summary>One value of a compared row.</summary>
public sealed record CompareCell(PresentedValue Value, object? Raw, ChangeStage? Stage)
{
    public bool IsNull => Raw is null;
}

/// <summary>A column across the compared rows.</summary>
/// <param name="Cells">One per row, in the order of the rows.</param>
/// <param name="Differs">Not all rows hold the same value (compared on the raw values, as Oracle would).</param>
public sealed record CompareField(int Column, ColumnInfo Info, ColumnLabel Label, IReadOnlyList<CompareCell> Cells, bool Differs)
{
    public bool AllNull => Cells.All(c => c.IsNull);
}

/// <summary>What the comparison shows: the search and its switches.</summary>
public sealed record CompareFilter(string? Query = null, bool HideEmpty = false, bool OnlyDifferences = false);

/// <summary>
/// Selected rows side by side (WP-21, read-only): which columns differ. Compared on the current values (pending and
/// flushed changes laid over, like the grid shows them), not on display texts – the building block the audit mode
/// of the backlog needs as well. LOBs are known only as preview and length: equal previews of equal length count as
/// equal.
/// </summary>
public static class RowComparison
{
    /// <summary>At most this many rows side by side; more get unreadable even with scrolling.</summary>
    public const int MaxRows = 20;

    public static IReadOnlyList<CompareField> Compare(
        TableDetails table, TablePresentation presentation, IReadOnlyList<FormRow> rows, IReadOnlyList<string> pinned) =>
        ColumnPinning.DisplayOrder(table, pinned)
            .Select(i =>
            {
                var cells = rows.Select(r => r.ValueOf(i)).Select((raw, r) => new CompareCell(presentation.Present(i, raw), raw, rows[r].Change?.StageOf(i))).ToList();
                var differs = cells.Skip(1).Any(c => !OracleTypeMapper.ValuesEqual(cells[0].Raw, c.Raw));
                return new CompareField(i, table.Columns[i], presentation.LabelOf(i), cells, differs);
            })
            .ToList();

    /// <summary>The columns matching the search and the switches; "leere" are columns NULL in every row.</summary>
    public static FormView<CompareField> Visible(IReadOnlyList<CompareField> fields, TablePresentation presentation, CompareFilter filter)
    {
        var found = RowForm.Matches(presentation, filter.Query);
        var matching = fields.Where(f => found.Contains(f.Column) && (!filter.OnlyDifferences || f.Differs)).ToList();
        if (!filter.HideEmpty)
        {
            return new FormView<CompareField>(matching, fields.Count, 0);
        }

        var shown = matching.Where(f => !f.AllNull).ToList();
        return new FormView<CompareField>(shown, fields.Count, matching.Count - shown.Count);
    }
}
