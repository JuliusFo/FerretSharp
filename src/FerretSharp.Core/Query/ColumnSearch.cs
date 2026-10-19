using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Query;

/// <summary>
/// Finds columns by name for the column picker (filter bar) and the column jump (Ctrl+F), ranked by
/// <see cref="NameRank"/>: every whitespace-separated term must occur in the name, ignoring case; underscores may be left
/// out ("lieferort" finds LIEFER_ORT).
/// </summary>
public static class ColumnSearch
{
    /// <summary>
    /// Indexes into <paramref name="columns"/> of the matches: exact name first, then names starting with the first
    /// term, then names where every term starts a word (after "_"), then the rest – each group in schema order.
    /// An empty query returns all columns in schema order.
    /// </summary>
    /// <param name="alternateName">
    /// A second name per column, matched the same way (the C# property name, WP-12: <c>KundeId</c> is found by
    /// "kundeid" and "id" starts a word); the better match of both names counts.
    /// </param>
    public static IReadOnlyList<int> Find(IReadOnlyList<ColumnInfo> columns, string? query, Func<int, string?>? alternateName = null)
    {
        var terms = NameRank.Terms(query);
        if (terms.Length == 0)
        {
            return Enumerable.Range(0, columns.Count).ToList();
        }

        return columns
            .Select((c, i) => (Index: i, Rank: NameRank.Best(NameRank.Of(c.Name, terms), alternateName?.Invoke(i) is { } alternate ? NameRank.OfClrName(alternate, terms) : null)))
            .Where(x => x.Rank is not null)
            .OrderBy(x => x.Rank) // stable: schema order within a rank
            .Select(x => x.Index)
            .ToList();
    }
}
