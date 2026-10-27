using FerretSharp.Core.Audit;
using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Data;
using FerretSharp.Core.Display;
using FerretSharp.Core.Oracle;

namespace FerretSharp.UI.State;

/// <summary>
/// The rows of a table grid as grid.js gets them (R3b, out of FerretGrid – a contract with grid.js, now with tests):
/// cells by column id (<see cref="GridColumn.IdOf"/>) as display text, null for NULL, plus markers –
/// <c>__s</c> stage per changed cell ("p" pending, "f" flushed), <c>__d</c> deleted ("p"/"f"), <c>__ro</c> cells that cannot
/// be edited, <c>__u</c> values the C# model does not know (an enum without member), <c>__t</c> tooltips, <c>__new</c> the
/// id of a new row not yet inserted, <c>__n</c> the display value beside a foreign key value and <c>__nm</c> foreign key
/// values without a row in their target (WP-31), <c>__c</c> cells of an audit row changed against the version before and
/// <c>__a</c> its action ("i" insert, "d" delete; WP-63).
/// </summary>
public static class GridRows
{
    /// <param name="change">The row's changes; values and stages come from it if set.</param>
    /// <param name="values">The loaded values; ignored if <paramref name="change"/> is set.</param>
    /// <param name="names">Names looked up for foreign key values (WP-31); null without.</param>
    /// <param name="audit">How a row of an audit table is marked (WP-63); null for other tables and unmarked rows.</param>
    public static Dictionary<string, object?> Build(
        TablePresentation presentation, RowChange? change, IReadOnlyList<object?>? values, DisplayNames? names = null, AuditMarks? audit = null)
    {
        var columns = presentation.Details.Columns.Count;
        var cells = new Dictionary<string, object?>(columns + 5);
        var presentedCells = new PresentedCells(cells);
        var stages = new Dictionary<string, string>();
        var readOnly = new List<string>();
        Dictionary<string, string>? displayNames = null;
        List<string>? notFound = null;
        for (var i = 0; i < columns; i++)
        {
            var id = GridColumn.IdOf(i);
            var value = change is not null ? change.ValueOf(i) : values?[i];
            presentedCells.Add(id, presentation.Present(i, value));

            switch (names?.NameOf(i, value))
            {
                case { NotFound: true }:
                    (notFound ??= []).Add(id);
                    break;
                case { Text: { } name }:
                    (displayNames ??= [])[id] = name;
                    break;
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

        presentedCells.Finish();
        if (displayNames is not null)
        {
            cells["__n"] = displayNames;
        }

        if (notFound is not null)
        {
            cells["__nm"] = notFound;
        }

        if (change is { IsNew: true, IsInserted: false })
        {
            cells["__new"] = NewRowId(change);
        }

        if (audit is not null)
        {
            if (audit.Changed.Count > 0)
            {
                cells["__c"] = audit.Changed.Order().Select(GridColumn.IdOf).ToList();
            }

            if (audit.Action is AuditActionKind.Insert or AuditActionKind.Delete)
            {
                cells["__a"] = audit.Action == AuditActionKind.Insert ? "i" : "d";
            }
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

    /// <summary>
    /// The presented values of a row for grid.js: the text by column id, <c>__u</c> values the C# model does not know,
    /// <c>__t</c> tooltips (R6b, review 3.40.0, B11: the table grid and the result grid collected them each).
    /// </summary>
    public sealed class PresentedCells(Dictionary<string, object?> cells)
    {
        private List<string>? _unknown;
        private Dictionary<string, string>? _tooltips;

        public void Add(string id, PresentedValue presented)
        {
            cells[id] = presented.Text;
            if (presented.Unknown)
            {
                (_unknown ??= []).Add(id);
            }

            if (presented.Tooltip is { } tooltip)
            {
                (_tooltips ??= [])[id] = tooltip;
            }
        }

        /// <summary>Adds the lists, if any; returns the cells.</summary>
        public Dictionary<string, object?> Finish()
        {
            if (_unknown is not null)
            {
                cells["__u"] = _unknown;
            }

            if (_tooltips is not null)
            {
                cells["__t"] = _tooltips;
            }

            return cells;
        }
    }

    private static string Stage(ChangeStage stage) => stage == ChangeStage.Pending ? "p" : "f";
}
