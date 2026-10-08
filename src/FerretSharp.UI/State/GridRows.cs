using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Data;
using FerretSharp.Core.Oracle;

namespace FerretSharp.UI.State;

/// <summary>
/// The rows of a table grid as grid.js gets them (R3b, out of FerretGrid – a contract with grid.js, now with tests):
/// cells by column id (<see cref="GridColumn.IdOf"/>) as display text, null for NULL, plus markers –
/// <c>__s</c> stage per changed cell ("p" pending, "f" flushed), <c>__d</c> deleted ("p"/"f"), <c>__ro</c> cells that cannot
/// be edited, <c>__u</c> values the C# model does not know (an enum without member), <c>__t</c> tooltips, <c>__new</c> the
/// id of a new row not yet inserted.
/// </summary>
public static class GridRows
{
    /// <param name="change">The row's changes; values and stages come from it if set.</param>
    /// <param name="values">The loaded values; ignored if <paramref name="change"/> is set.</param>
    public static Dictionary<string, object?> Build(TablePresentation presentation, RowChange? change, IReadOnlyList<object?>? values)
    {
        var columns = presentation.Details.Columns.Count;
        var cells = new Dictionary<string, object?>(columns + 5);
        var stages = new Dictionary<string, string>();
        var readOnly = new List<string>();
        var unknown = new List<string>();
        Dictionary<string, string>? tooltips = null;
        for (var i = 0; i < columns; i++)
        {
            var id = GridColumn.IdOf(i);
            var value = change is not null ? change.ValueOf(i) : values?[i];
            var presented = presentation.Present(i, value);
            cells[id] = presented.Text;
            if (presented.Unknown)
            {
                unknown.Add(id);
            }

            if (presented.Tooltip is { } tooltip)
            {
                (tooltips ??= [])[id] = tooltip;
            }

            if (change?.StageOf(i) is { } stage)
            {
                stages[id] = Stage(stage);
            }

            if (!OracleTypeMapper.IsEditableValue(value))
            {
                readOnly.Add(id);
            }
        }

        if (stages.Count > 0)
        {
            cells["__s"] = stages;
        }

        if (change?.Deleted is { } deleted)
        {
            cells["__d"] = Stage(deleted);
        }

        if (readOnly.Count > 0)
        {
            cells["__ro"] = readOnly;
        }

        if (unknown.Count > 0)
        {
            cells["__u"] = unknown;
        }

        if (tooltips is not null)
        {
            cells["__t"] = tooltips;
        }

        if (change is { IsNew: true, IsInserted: false })
        {
            cells["__new"] = NewRowId(change);
        }

        return cells;
    }

    /// <summary>How grid.js and the form know a new row (pinned at the top until written).</summary>
    public static string NewRowId(RowChange row) => row.Id.ToString("N");

    /// <summary>The new row with that id; null if there is none (written meanwhile, or no id).</summary>
    public static RowChange? NewRow(ChangeTracker? changes, string? newId) =>
        newId is null ? null : changes?.NewRows.FirstOrDefault(r => NewRowId(r) == newId);

    /// <summary>The position of a new row among the pinned ones; -1 if it is not there.</summary>
    public static int NewRowIndex(ChangeTracker? changes, string? newId) =>
        newId is null || changes is null ? -1 : changes.NewRows.ToList().FindIndex(r => NewRowId(r) == newId);

    private static string Stage(ChangeStage stage) => stage == ChangeStage.Pending ? "p" : "f";
}
