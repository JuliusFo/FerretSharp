using System.Globalization;
using FerretSharp.Core.Data;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Query;

public enum JumpDirection
{
    /// <summary>To the row this row references (FK column → referenced table).</summary>
    Outgoing,

    /// <summary>To the rows that reference this row (referencing table filtered by its FK columns).</summary>
    Incoming,
}

/// <summary>A jump from one or more rows along a foreign key.</summary>
/// <param name="Table">Table to open: the referenced table (outgoing) or the referencing one (incoming).</param>
/// <param name="Filters">
/// Filters on the key columns of <paramref name="Table"/>: equality per column if all rows share one key, otherwise
/// one IN list; empty if unavailable.
/// </param>
/// <param name="Unavailable">Why the jump is not possible (NULL key, unsupported type, too many values); null if it is.</param>
/// <param name="Rows">Rows the jump was built from.</param>
/// <param name="SkippedRows">Rows left out because their key is NULL.</param>
public sealed record FkJump(
    ForeignKeyInfo ForeignKey, JumpDirection Direction, TableRef Table, IReadOnlyList<FilterCondition> Filters, string? Unavailable,
    int Rows = 1, int SkippedRows = 0)
{
    private const int ShownValues = 3;

    public bool IsAvailable => Unavailable is null;

    /// <summary>An incoming jump into a base table of the row's table (<see cref="BaseTables"/>); null for a direct one.</summary>
    public InheritedReference? Inherited { get; init; }

    /// <summary>The columns of the row's table the jump takes its values from.</summary>
    public IReadOnlyList<string> RowColumns =>
        Inherited?.Columns ?? (Direction == JumpDirection.Outgoing ? ForeignKey.FromColumns : ForeignKey.ToColumns);

    /// <summary>The key condition, e.g. <c>KUNDE_ID = 4711</c> or <c>KUNDE_ID in (4711; 4712; 4713; …) · 5 values</c>.</summary>
    public string Condition => string.Join(", ", Filters.Select(f => f.Op == FilterOperator.In
        ? TextFormat.Format(FkNavigation.German, QueryText.FkConditionInList, f.Column,
            string.Join("; ", f.Values.Take(ShownValues)) + (f.Values.Count > ShownValues ? "; …" : ""), f.Values.Count)
        : $"{f.Column} = {f.Values[0]}"));

    /// <summary>E.g. "2 rows without a value skipped"; null if every row had a key.</summary>
    public string? SkippedNote => SkippedRows == 0
        ? null
        : TextFormat.Plural(FkNavigation.German, SkippedRows, QueryText.FkSkippedRowsOne, QueryText.FkSkippedRowsOther);
}

/// <summary>Filters on a key built from rows (<see cref="FkNavigation.KeyFilters"/>); empty with the reason if not possible.</summary>
/// <param name="SkippedRows">Rows left out because their key is NULL.</param>
public sealed record KeyFilter(IReadOnlyList<FilterCondition> Filters, string? Unavailable, int SkippedRows);

/// <summary>
/// FK navigation: turns the key values of one or more rows into filters for the related table. Filter values are
/// written from the raw values (never from the German display text, where "1.234" would read as 1.234) in a form the
/// filter parses back exactly.
/// </summary>
public static class FkNavigation
{
    /// <summary>Incoming counts give up after this; FK columns are often not indexed.</summary>
    public static readonly TimeSpan CountTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// A jump from several rows lists at most this many distinct values (one IN list; ORA-01795). More would make the
    /// filter bar, the tab title and the workspace file unwieldy.
    /// </summary>
    public const int MaxValues = 1000;

    internal static readonly CultureInfo German = ValueCulture.German;

    /// <summary>
    /// Outgoing jumps first, then incoming ones, then those into base tables (<paramref name="inherited"/>, nearest base table
    /// first); each group ordered by table and constraint name.
    /// </summary>
    public static IReadOnlyList<FkJump> JumpsFor(
        TableDetails table, RowData row, IEnumerable<ForeignKeyInfo> outgoing, IEnumerable<ForeignKeyInfo> incoming,
        IEnumerable<InheritedReference>? inherited = null) =>
        JumpsFor(table, [row], outgoing, incoming, inherited);

    /// <inheritdoc cref="JumpsFor(TableDetails, RowData, IEnumerable{ForeignKeyInfo}, IEnumerable{ForeignKeyInfo}, IEnumerable{InheritedReference})"/>
    /// <param name="rows">The selected rows (at least one), in selection order.</param>
    public static IReadOnlyList<FkJump> JumpsFor(
        TableDetails table, IReadOnlyList<RowData> rows, IEnumerable<ForeignKeyInfo> outgoing, IEnumerable<ForeignKeyInfo> incoming,
        IEnumerable<InheritedReference>? inherited = null) =>
        outgoing.Select(fk => Outgoing(table, rows, fk)).OrderBy(Key, StringComparer.Ordinal)
            .Concat(incoming.Select(fk => Incoming(table, rows, fk)).OrderBy(Key, StringComparer.Ordinal))
            .Concat((inherited ?? []).Select(r => Inherited(table, rows, r)).GroupBy(j => j.Inherited!.Via)
                .SelectMany(g => g.OrderBy(Key, StringComparer.Ordinal)))
            .ToList();

    /// <summary><paramref name="row"/> belongs to <c>fk.From</c>; opens <c>fk.To</c> where its key equals the row's FK values.</summary>
    public static FkJump Outgoing(TableDetails table, RowData row, ForeignKeyInfo fk) => Outgoing(table, [row], fk);

    /// <summary>
    /// <paramref name="rows"/> belong to <c>fk.From</c>; opens <c>fk.To</c> where its key is one of the rows' FK values.
    /// </summary>
    public static FkJump Outgoing(TableDetails table, IReadOnlyList<RowData> rows, ForeignKeyInfo fk) =>
        Build(table, rows, fk, JumpDirection.Outgoing, fk.FromColumns, fk.To, fk.ToColumns);

    /// <summary><paramref name="row"/> belongs to <c>fk.To</c>; opens <c>fk.From</c> where its FK columns equal the row's key.</summary>
    public static FkJump Incoming(TableDetails table, RowData row, ForeignKeyInfo fk) => Incoming(table, [row], fk);

    /// <summary>
    /// <paramref name="rows"/> belong to <c>fk.To</c>; opens <c>fk.From</c> where its FK columns hold one of the rows' keys.
    /// </summary>
    public static FkJump Incoming(TableDetails table, IReadOnlyList<RowData> rows, ForeignKeyInfo fk) =>
        Build(table, rows, fk, JumpDirection.Incoming, fk.ToColumns, fk.From, fk.FromColumns);

    /// <summary>
    /// <paramref name="rows"/> belong to an extension table of <c>reference.Via</c>; opens <c>fk.From</c> where its FK columns
    /// hold one of the rows' keys, taken from the extension table's columns that share the base table's key.
    /// </summary>
    public static FkJump Inherited(TableDetails table, IReadOnlyList<RowData> rows, InheritedReference reference) =>
        Build(table, rows, reference.ForeignKey, JumpDirection.Incoming, reference.Columns, reference.ForeignKey.From, reference.ForeignKey.FromColumns)
            with { Inherited = reference };

    /// <summary>
    /// Filter text that parses back to exactly <paramref name="value"/>: invariant numbers, ISO dates, hex for RAW.
    /// Null if the type cannot be compared for equality (LOBs, intervals, floats, NUMBER beyond 28 digits …).
    /// </summary>
    public static string? FilterValue(ColumnInfo column, object value) => (ColumnCategories.Of(column), value) switch
    {
        (ColumnCategory.Number, decimal d) => d.ToString(CultureInfo.InvariantCulture),
        (ColumnCategory.Number, int or long or short) => Convert.ToString(value, CultureInfo.InvariantCulture),
        (ColumnCategory.Date, DateTime dt) => dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        (ColumnCategory.Timestamp, DateTime dt) => dt.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture),
        // A CHAR value without its padding (review 3.31.0, A32): a CHAR target compares blank-padded anyway, a VARCHAR2 one
        // (a relationship of the C# model or by convention) found nothing with it.
        (ColumnCategory.Text, string s) when s.Length > 0 => OracleTypes.IsFixedChar(column.DataType) && s.TrimEnd(' ') is { Length: > 0 } trimmed ? trimmed : s,
        (ColumnCategory.Raw, byte[] bytes) when bytes.Length > 0 => Convert.ToHexString(bytes),
        _ => null,
    };

    /// <summary>
    /// Rows the jump would show, or null if counting took longer than <paramref name="timeout"/>. The statement is
    /// then cancelled on the server; the result returns at the timeout even if the server takes a moment to stop
    /// (the session stays busy until it has). Cancelling <paramref name="cancellationToken"/> throws as usual.
    /// </summary>
    public static Task<long?> CountAsync(
        IDataAccess data, TableDetails target, FkJump jump, TimeSpan timeout, CancellationToken cancellationToken) =>
        TimedQuery.RunAsync<long?>(async token => await data.CountAsync(target, jump.Filters, token), timeout, cancellationToken);

    /// <summary>
    /// Rows with a NULL in a key column are skipped. If all remaining rows share one key: equality per column (as for a
    /// single row); otherwise one IN list – only for single-column keys, since the filter model has no tuple IN (it has
    /// to translate 1:1 into a LINQ <c>Where</c>).
    /// </summary>
    private static FkJump Build(
        TableDetails table, IReadOnlyList<RowData> rows, ForeignKeyInfo fk, JumpDirection direction,
        IReadOnlyList<string> rowColumns, TableRef target, IReadOnlyList<string> targetColumns)
    {
        var key = KeyFilters(table, rows, rowColumns, targetColumns);
        return new FkJump(fk, direction, target, key.Filters, key.Unavailable, rows.Count, key.SkippedRows);
    }

    /// <summary>
    /// Filters on <paramref name="targetColumns"/> for the values of <paramref name="rowColumns"/> in <paramref name="rows"/>:
    /// equality per column if all rows share one key, otherwise one IN list (single-column keys only). Rows with a NULL in
    /// the key are skipped. Also the key of a link to selected rows (WP-37), with the table's own key on both sides.
    /// </summary>
    public static KeyFilter KeyFilters(TableDetails table, IReadOnlyList<RowData> rows, IReadOnlyList<string> rowColumns, IReadOnlyList<string> targetColumns)
    {
        var skipped = 0;
        KeyFilter Unavailable(string reason) => new([], reason, skipped);

        var indexes = new int[rowColumns.Count];
        for (var i = 0; i < rowColumns.Count; i++)
        {
            indexes[i] = table.IndexOf(rowColumns[i]);
            if (indexes[i] < 0)
            {
                return Unavailable(TextFormat.Format(QueryText.FkColumnMissing, rowColumns[i]));
            }
        }

        var keys = new List<string[]>(rows.Count);
        string? nullColumn = null;
        foreach (var row in rows)
        {
            string[]? key = new string[indexes.Length];
            for (var i = 0; i < indexes.Length && key is not null; i++)
            {
                var index = indexes[i];
                if (row.Values[index] is not { } value)
                {
                    nullColumn ??= rowColumns[i];
                    key = null;
                }
                else if (FilterValue(table.Columns[index], value) is { } text)
                {
                    key[i] = text;
                }
                else
                {
                    return Unavailable(TextFormat.Format(QueryText.FkJumpTypeNotSupported, table.Columns[index].DisplayType));
                }
            }

            if (key is null)
            {
                skipped++;
            }
            else
            {
                keys.Add(key);
            }
        }

        if (keys.Count == 0)
        {
            return Unavailable(rows.Count == 1 ? TextFormat.Format(QueryText.FkKeyIsNull, nullColumn)
                : rowColumns.Count == 1 ? TextFormat.Format(German, QueryText.FkColumnNullInAllRows, rowColumns[0], rows.Count)
                : TextFormat.Format(German, QueryText.FkKeyNullInAllRows, string.Join(", ", rowColumns), rows.Count));
        }

        if (keys.All(k => k.SequenceEqual(keys[0], StringComparer.Ordinal)))
        {
            return new([.. targetColumns.Select((column, i) => FilterCondition.Of(column, FilterOperator.Equals, keys[0][i]))], null, skipped);
        }

        if (rowColumns.Count > 1)
        {
            return Unavailable(QueryText.FkSeveralRowsNeedSingleColumnKey);
        }

        var values = keys.Select(k => k[0]).Distinct(StringComparer.Ordinal).ToArray();
        if (values.Length > MaxValues)
        {
            return Unavailable(TextFormat.Format(German, QueryText.FkTooManyValues, values.Length, MaxValues));
        }

        // The filter bar splits an IN list at the separator; such a value would not survive editing the filter.
        if (values.Any(v => v.Contains(FilterCondition.ListSeparator, StringComparison.Ordinal)))
        {
            return Unavailable(TextFormat.Format(QueryText.FkValueContainsSeparator, FilterCondition.ListSeparator));
        }

        return new([FilterCondition.Of(targetColumns[0], FilterOperator.In, values)], null, skipped);
    }

    private static string Key(FkJump jump) => $"{jump.Table.Name}\0{jump.Table.Owner}\0{jump.ForeignKey.Name}";
}
