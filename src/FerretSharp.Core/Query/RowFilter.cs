using System.Collections;
using System.Runtime.CompilerServices;

namespace FerretSharp.Core.Query;

/// <summary>
/// Which rows of a table a tab reads (WP-79, ADR 0049): the rows of the filter bar (<see cref="Conditions"/>) or a condition
/// the user wrote in SQL (<see cref="Sql"/>) – never both. The condition is no <see cref="FilterCondition"/>: the filter model
/// stays what a LINQ <c>Where</c> can say, so a link, a preset or LINQ only ever take <see cref="Conditions"/>.
/// <para>
/// A list of filter rows becomes a filter of its own: <c>[]</c> and <c>[a, b]</c> build one (<see cref="Create"/>), a list or an
/// array converts; enumerating it yields <see cref="Conditions"/> (none for a SQL condition).
/// </para>
/// </summary>
[CollectionBuilder(typeof(RowFilter), nameof(Create))]
public sealed class RowFilter : IEnumerable<FilterCondition>, IEquatable<RowFilter>
{
    private RowFilter(IReadOnlyList<FilterCondition> conditions, string? sql)
    {
        Conditions = conditions;
        Sql = sql;
    }

    /// <summary>No filter: every row.</summary>
    public static RowFilter None { get; } = new([], null);

    /// <summary>The filter rows; empty for a SQL condition.</summary>
    public IReadOnlyList<FilterCondition> Conditions { get; }

    /// <summary>The condition as the user wrote it, without <c>WHERE</c> (<see cref="WhereCondition"/>); null for filter rows.</summary>
    public string? Sql { get; }

    public bool IsSql => Sql is not null;

    public static RowFilter Create(ReadOnlySpan<FilterCondition> conditions) => conditions.IsEmpty ? None : new([.. conditions], null);

    public static RowFilter Of(IReadOnlyList<FilterCondition> conditions)
    {
        ArgumentNullException.ThrowIfNull(conditions);
        return conditions.Count == 0 ? None : new(conditions, null);
    }

    /// <summary>A condition written in SQL; checked when a statement is built (<see cref="WhereCondition.Problem"/>).</summary>
    public static RowFilter Where(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        return new([], sql);
    }

    public static implicit operator RowFilter(List<FilterCondition> conditions) => Of(conditions);

    public static implicit operator RowFilter(FilterCondition[] conditions) => Of(conditions);

    public IEnumerator<FilterCondition> GetEnumerator() => Conditions.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public bool Equals(RowFilter? other) =>
        other is not null && Sql == other.Sql && Conditions.SequenceEqual(other.Conditions);

    public override bool Equals(object? obj) => Equals(obj as RowFilter);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Sql);
        foreach (var condition in Conditions)
        {
            hash.Add(condition);
        }

        return hash.ToHashCode();
    }

    public override string ToString() => Sql is { } sql ? "WHERE " + sql : $"{Conditions.Count} filter row(s)";
}
