using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Data;
using FerretSharp.Core.Oracle;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;
using FerretSharp.Core.Workspaces;

namespace FerretSharp.Core.Forms;

/// <summary>
/// The row a form shows (WP-21): a loaded row with the tab's changes laid over it, or a new row that is not inserted
/// yet (<see cref="Loaded"/> null).
/// </summary>
public sealed record FormRow(RowData? Loaded, RowChange? Change)
{
    public bool IsNew => Loaded is null;

    /// <summary>Current value: pending, else flushed, else as loaded – what the grid shows.</summary>
    public object? ValueOf(int column) => Change is not null ? Change.ValueOf(column) : Loaded?.Values[column];

    /// <summary>The row with its current values (FK jumps use what the user just typed, decision of the user).</summary>
    public RowData Current(int columns) =>
        new(Change?.Key ?? Loaded?.Key ?? RowKey.None.Instance, Enumerable.Range(0, columns).Select(ValueOf).ToList());

    /// <summary>Primary key values as text (<c>8</c>, <c>1 · 2</c>); null without a primary key or for a new row.</summary>
    public string? KeyText(TableDetails table)
    {
        if (IsNew || table.PrimaryKey.Count == 0)
        {
            return null;
        }

        var texts = table.PrimaryKey
            .Select(name => table.Columns.ToList().FindIndex(c => c.Name == name))
            .Where(i => i >= 0)
            .Select(i => CellFormatter.Format(table.Columns[i], ValueOf(i)) ?? "NULL");
        return string.Join(" · ", texts);
    }
}

/// <summary>One field of the form view: a column of the shown row.</summary>
/// <param name="Value">The display text (enum members by name, NULL as null), as in the grid.</param>
/// <param name="Raw">The current value (pending or flushed laid over the loaded one).</param>
/// <param name="EditText">The text the editor starts with: the full value; for enum and bool columns the member's database value.</param>
/// <param name="Stage">Pending or flushed change of this cell; null if unchanged.</param>
/// <param name="ReadOnlyReason">Why the value cannot be changed here; null if it can (LOBs: in the LOB editor).</param>
/// <param name="Options">Members of an enum or converted bool column (picked instead of typed); null for other columns.</param>
/// <param name="Jumps">Outgoing foreign keys over this column, built from the current values.</param>
/// <param name="Mismatches">Differences between the column and its C# property (WP-27).</param>
public sealed record FormField(
    int Column,
    ColumnInfo Info,
    ColumnLabel Label,
    PresentedValue Value,
    object? Raw,
    string EditText,
    ChangeStage? Stage,
    string? ReadOnlyReason,
    bool IsPrimaryKey,
    bool IsUnique,
    ValueTable? Options,
    IReadOnlyList<FkJump> Jumps,
    IReadOnlyList<ColumnMismatch> Mismatches)
{
    public bool IsNull => Raw is null;

    public bool IsEditable => ReadOnlyReason is null;

    /// <summary>CLOB/NCLOB/BLOB: shown as preview, opened (and changed) in the LOB editor.</summary>
    public bool IsLob => OracleTypeMapper.IsLob(Info);

    /// <summary>Long texts get a text area, like the multi-line cell editor of the grid.</summary>
    public bool IsMultiline => ColumnCategories.Of(Info) == ColumnCategory.Text && (Info.Length ?? 0) > 200;
}

/// <summary>What the form shows of its fields: the search (also C# names) and the switches of the panel.</summary>
/// <param name="HideEmpty">Hide NULL fields (a changed field always stays).</param>
/// <param name="OnlyChanged">Only fields with a pending or flushed change.</param>
public sealed record FormFilter(string? Query = null, bool HideEmpty = false, bool OnlyChanged = false);

/// <summary>The shown fields and how many the switches hide.</summary>
/// <param name="EmptyHidden">Fields hidden by "leere ausblenden" alone (they match the search).</param>
public sealed record FormView<T>(IReadOnlyList<T> Fields, int Total, int EmptyHidden);

/// <summary>
/// Form view of one row (WP-21): every column as a field with label, type, value, change stage, why it is read-only,
/// FK jumps and model mismatches. Pure; the UI edits through the same <see cref="ChangeTracker"/> as the grid.
/// </summary>
public static class RowForm
{
    /// <summary>The fields in the grid's display order (primary key, pinned columns, the rest in schema order).</summary>
    /// <param name="writable">The workspace may write and the tab tracks changes (as the grid's editing).</param>
    /// <param name="outgoing">Outgoing foreign keys of the table (declared and from the C# model).</param>
    /// <param name="mapping">The C# model for mismatches (WP-27); null without one.</param>
    public static IReadOnlyList<FormField> Fields(
        TableDetails table, TablePresentation presentation, FormRow row, bool writable, IEnumerable<ForeignKeyInfo> outgoing,
        ClrModelMapping? mapping, IReadOnlyList<string> pinned)
    {
        var current = row.Current(table.Columns.Count);
        var jumps = outgoing.Select(fk => (Fk: fk, Jump: FkNavigation.Outgoing(table, current, fk))).ToList();
        var unique = table.UniqueKeys.SelectMany(k => k).ToHashSet(StringComparer.Ordinal);
        return ColumnPinning.DisplayOrder(table, pinned)
            .Select(i =>
            {
                var info = table.Columns[i];
                var raw = row.ValueOf(i);
                var options = presentation.ValuesOf(i);
                return new FormField(
                    i,
                    info,
                    presentation.LabelOf(i),
                    presentation.Present(i, raw),
                    raw,
                    options?.OptionOf(raw)?.Value ?? OracleTypeMapper.EditText(info, raw),
                    row.Change?.StageOf(i),
                    ReadOnlyReason(table, info, row, raw, writable),
                    table.PrimaryKey.Contains(info.Name),
                    unique.Contains(info.Name),
                    options,
                    jumps.Where(j => j.Fk.FromColumns.Contains(info.Name)).Select(j => j.Jump).ToList(),
                    mapping?.MismatchesOf(table.Table.Ref, info.Name).ToList() ?? []);
            })
            .ToList();
    }

    /// <summary>Rows referencing this one, per incoming foreign key; none for a new row (nothing can reference it yet).</summary>
    public static IReadOnlyList<FkJump> Incoming(TableDetails table, FormRow row, IEnumerable<ForeignKeyInfo> incoming) =>
        row.IsNew
            ? []
            : FkNavigation.JumpsFor(table, row.Current(table.Columns.Count), [], incoming);

    /// <summary>The fields matching the search (column or C# property name) and the switches, in their order.</summary>
    public static FormView<FormField> Visible(IReadOnlyList<FormField> fields, TablePresentation presentation, FormFilter filter)
    {
        var found = Matches(presentation, filter.Query);
        var matching = fields.Where(f => found.Contains(f.Column) && (!filter.OnlyChanged || f.Stage is not null)).ToList();
        if (!filter.HideEmpty)
        {
            return new FormView<FormField>(matching, fields.Count, 0);
        }

        // A field the user changed stays, also when it became NULL.
        var shown = matching.Where(f => !f.IsNull || f.Stage is not null).ToList();
        return new FormView<FormField>(shown, fields.Count, matching.Count - shown.Count);
    }

    /// <summary>Column indexes matching the search, like Ctrl+F (C# names only while they are shown).</summary>
    internal static HashSet<int> Matches(TablePresentation presentation, string? query) =>
        ColumnSearch.Find(presentation.Details.Columns, query, presentation.SearchNameOf).ToHashSet();

    private static string? ReadOnlyReason(TableDetails table, ColumnInfo info, FormRow row, object? raw, bool writable)
    {
        // A loaded row exists in the database (also one inserted in this transaction): its key stays as it is.
        if (OracleTypeMapper.NotEditableReason(table, info, newRow: row.IsNew) is { } reason)
        {
            return reason;
        }

        if (!writable)
        {
            return "Der Workspace ist schreibgeschützt – zum Ändern freischalten.";
        }

        if (row.Change?.Deleted is not null)
        {
            return "Die Zeile ist zum Löschen markiert.";
        }

        return OracleTypeMapper.IsEditableValue(raw) ? null : "Zahlen mit mehr als 28 Stellen lassen sich hier nicht bearbeiten.";
    }
}
