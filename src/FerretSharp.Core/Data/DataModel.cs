using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Data;

/// <summary>Identifies a row for navigation (v1) and later for UPDATE/DELETE (v2). See CLAUDE.md 5.5.</summary>
public abstract record RowKey
{
    /// <summary>Compares by value: two keys with the same column values are equal.</summary>
    public sealed record PrimaryKey(IReadOnlyList<object?> Values) : RowKey
    {
        public bool Equals(PrimaryKey? other) => other is not null && Values.SequenceEqual(other.Values);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            foreach (var value in Values)
            {
                hash.Add(value);
            }

            return hash.ToHashCode();
        }
    }

    public sealed record RowId(string Value) : RowKey;

    public sealed record None : RowKey
    {
        public static readonly None Instance = new();
    }
}

/// <summary>A NUMBER that does not fit into <see cref="decimal"/> (up to 38 digits); kept as invariant text.</summary>
public sealed record BigNumber(string Invariant)
{
    public override string ToString() => Invariant;
}

/// <summary>CLOB: first characters plus total length. BLOB: length only (<see cref="Preview"/> is null).</summary>
public sealed record LobValue(string? Preview, long Length);

/// <summary>Value of a column that is only checked for NULL (LONG, XMLTYPE, object types …).</summary>
public sealed record NotNullMarker(string DataType);

public sealed record RowData(RowKey Key, IReadOnlyList<object?> Values);

/// <param name="IsLastPage">Fewer rows than requested were returned.</param>
public sealed record RowPage(IReadOnlyList<RowData> Rows, bool IsLastPage, TimeSpan Elapsed);

public interface IDataAccess
{
    /// <summary>Reads one page; values are in <see cref="TableDetails.Columns"/> order.</summary>
    /// <exception cref="QueryValidationException">A filter or sort cannot be applied.</exception>
    Task<RowPage> ReadPageAsync(
        TableDetails table,
        IReadOnlyList<FilterCondition> filters,
        IReadOnlyList<SortSpec> sorts,
        PageSpec page,
        CancellationToken cancellationToken);

    Task<long> CountAsync(TableDetails table, IReadOnlyList<FilterCondition> filters, CancellationToken cancellationToken);
}
