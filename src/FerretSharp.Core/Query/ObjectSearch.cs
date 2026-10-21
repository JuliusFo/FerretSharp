using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Query;

/// <summary>
/// Finds tables, views and PL/SQL units by name for the quick open (WP-38), ranked like the column search
/// (<see cref="NameRank"/>): a table by the name shown, its real name (synonym target) and its C# entity name
/// (<c>Kunde</c> finds <c>KUNDEN</c>), the better match counts. Within a rank the list order stays (by name shown).
/// </summary>
public static class ObjectSearch
{
    /// <summary>The matching tables, best first; none without a query.</summary>
    /// <param name="entityName">The C# entity name of a table; null if it has none (no model, not mapped).</param>
    public static IReadOnlyList<TableSummary> FindTables(IEnumerable<TableSummary> tables, string? query, Func<TableRef, string?>? entityName = null)
    {
        var terms = NameRank.Terms(query);
        return terms.Length == 0
            ? []
            : Ranked(tables, t => NameRank.Best(
                NameRank.Best(NameRank.Of(t.DisplayName, terms), NameRank.Of(t.Name, terms)),
                entityName?.Invoke(t.Ref) is { } entity ? NameRank.OfClrName(entity, terms) : null));
    }

    /// <summary>The matching PL/SQL units by the name shown and the real name, best first; none without a query.</summary>
    public static IReadOnlyList<PlSqlObjectSummary> FindPlSql(IEnumerable<PlSqlObjectSummary> units, string? query)
    {
        var terms = NameRank.Terms(query);
        return terms.Length == 0
            ? []
            : Ranked(units, u => NameRank.Best(NameRank.Of(u.DisplayName, terms), NameRank.Of(u.Name, terms)));
    }

    private static List<T> Ranked<T>(IEnumerable<T> items, Func<T, NameMatch?> rank) =>
        items
            .Select(item => (Item: item, Rank: rank(item)))
            .Where(x => x.Rank is not null)
            .OrderBy(x => x.Rank) // stable: list order within a rank
            .Select(x => x.Item)
            .ToList();
}
