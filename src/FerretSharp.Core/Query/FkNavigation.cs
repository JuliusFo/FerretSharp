using System.Globalization;
using FerretSharp.Core.Data;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Query;

public enum JumpDirection
{
    /// <summary>To the row this row references (FK column → referenced table).</summary>
    Outgoing,

    /// <summary>To the rows that reference this row (referencing table filtered by its FK columns).</summary>
    Incoming,
}

/// <summary>A jump from one row along a foreign key.</summary>
/// <param name="Table">Table to open: the referenced table (outgoing) or the referencing one (incoming).</param>
/// <param name="Filters">Equality filters on the key columns of <paramref name="Table"/>; empty if unavailable.</param>
/// <param name="Unavailable">Why the jump is not possible (NULL key, unsupported type); null if it is.</param>
public sealed record FkJump(
    ForeignKeyInfo ForeignKey, JumpDirection Direction, TableRef Table, IReadOnlyList<FilterCondition> Filters, string? Unavailable)
{
    public bool IsAvailable => Unavailable is null;

    /// <summary>The key condition, e.g. <c>KUNDE_ID = 4711</c>.</summary>
    public string Condition => string.Join(", ", Filters.Select(f => $"{f.Column} = {f.Values[0]}"));
}

/// <summary>
/// FK navigation: turns a row's key values into filters for the related table. Filter values are written from the
/// raw values (never from the German display text, where "1.234" would read as 1.234) in a form the filter parses
/// back exactly.
/// </summary>
public static class FkNavigation
{
    /// <summary>Incoming counts give up after this; FK columns are often not indexed.</summary>
    public static readonly TimeSpan CountTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Outgoing jumps first, then incoming ones; each group ordered by table and constraint name.</summary>
    public static IReadOnlyList<FkJump> JumpsFor(
        TableDetails table, RowData row, IEnumerable<ForeignKeyInfo> outgoing, IEnumerable<ForeignKeyInfo> incoming) =>
        outgoing.Select(fk => Outgoing(table, row, fk)).OrderBy(Key, StringComparer.Ordinal)
            .Concat(incoming.Select(fk => Incoming(table, row, fk)).OrderBy(Key, StringComparer.Ordinal))
            .ToList();

    /// <summary><paramref name="row"/> belongs to <c>fk.From</c>; opens <c>fk.To</c> where its key equals the row's FK values.</summary>
    public static FkJump Outgoing(TableDetails table, RowData row, ForeignKeyInfo fk) =>
        Build(table, row, fk, JumpDirection.Outgoing, fk.FromColumns, fk.To, fk.ToColumns);

    /// <summary><paramref name="row"/> belongs to <c>fk.To</c>; opens <c>fk.From</c> where its FK columns equal the row's key.</summary>
    public static FkJump Incoming(TableDetails table, RowData row, ForeignKeyInfo fk) =>
        Build(table, row, fk, JumpDirection.Incoming, fk.ToColumns, fk.From, fk.FromColumns);

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
        (ColumnCategory.Text, string s) when s.Length > 0 => s,
        (ColumnCategory.Raw, byte[] bytes) when bytes.Length > 0 => Convert.ToHexString(bytes),
        _ => null,
    };

    /// <summary>
    /// Rows the jump would show, or null if counting took longer than <paramref name="timeout"/>. The statement is
    /// then cancelled on the server; the result returns at the timeout even if the server takes a moment to stop
    /// (the session stays busy until it has). Cancelling <paramref name="cancellationToken"/> throws as usual.
    /// </summary>
    public static async Task<long?> CountAsync(
        IDataAccess data, TableDetails target, FkJump jump, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var count = data.CountAsync(target, jump.Filters, cts.Token);
        try
        {
            return await count.WaitAsync(timeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            await cts.CancelAsync();
            return null;
        }
        finally
        {
            // Dispose only once the statement has ended; its cancel registration uses the token.
            _ = count.ContinueWith(_ => cts.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private static FkJump Build(
        TableDetails table, RowData row, ForeignKeyInfo fk, JumpDirection direction,
        IReadOnlyList<string> rowColumns, TableRef target, IReadOnlyList<string> targetColumns)
    {
        var filters = new List<FilterCondition>(rowColumns.Count);
        string? unavailable = null;
        for (var i = 0; i < rowColumns.Count && unavailable is null; i++)
        {
            var index = IndexOf(table, rowColumns[i]);
            if (index < 0)
            {
                unavailable = $"Spalte {rowColumns[i]} fehlt.";
            }
            else if (row.Values[index] is not { } value)
            {
                unavailable = $"{rowColumns[i]} ist NULL.";
            }
            else if (FilterValue(table.Columns[index], value) is { } text)
            {
                filters.Add(FilterCondition.Of(targetColumns[i], FilterOperator.Equals, text));
            }
            else
            {
                unavailable = $"Sprung über {table.Columns[index].DisplayType} nicht möglich.";
            }
        }

        return new FkJump(fk, direction, target, unavailable is null ? filters : [], unavailable);
    }

    private static string Key(FkJump jump) => $"{jump.Table.Name}\0{jump.Table.Owner}\0{jump.ForeignKey.Name}";

    private static int IndexOf(TableDetails table, string column)
    {
        for (var i = 0; i < table.Columns.Count; i++)
        {
            if (table.Columns[i].Name == column)
            {
                return i;
            }
        }

        return -1;
    }
}
