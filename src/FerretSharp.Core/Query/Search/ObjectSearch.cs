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
        return terms.Length == 0 ? [] : Ranked(tables, t => RankOf(t, terms, entityName?.Invoke(t.Ref)));
    }

    /// <summary>The matching PL/SQL units by the name shown and the real name, best first; none without a query.</summary>
    public static IReadOnlyList<PlSqlObjectSummary> FindPlSql(IEnumerable<PlSqlObjectSummary> units, string? query)
    {
        var terms = NameRank.Terms(query);
        return terms.Length == 0 ? [] : Ranked(units, u => RankOf(u, terms));
    }

    /// <summary>
    /// The explorer's filter, by the same rule as the quick open (review 3.44.0, B4: "lieferort" found LIEFER_ORT with Ctrl+P,
    /// not in the explorer); true without a query. The list keeps its order.
    /// </summary>
    public static bool Matches(TableSummary table, string? query, string? entityName) =>
        NameRank.Terms(query) is var terms && (terms.Length == 0 || RankOf(table, terms, entityName) is not null);

    /// <inheritdoc cref="Matches(TableSummary, string?, string?)"/>
    public static bool Matches(PlSqlObjectSummary unit, string? query) =>
        NameRank.Terms(query) is var terms && (terms.Length == 0 || RankOf(unit, terms) is not null);

    private static NameMatch? RankOf(TableSummary table, IReadOnlyList<string> terms, string? entityName) => NameRank.Best(
        NameRank.Best(NameRank.Of(table.DisplayName, terms), NameRank.Of(table.Name, terms)),
        entityName is not null ? NameRank.OfClrName(entityName, terms) : null);

    private static NameMatch? RankOf(PlSqlObjectSummary unit, IReadOnlyList<string> terms) =>
        NameRank.Best(NameRank.Of(unit.DisplayName, terms), NameRank.Of(unit.Name, terms));

    private static List<T> Ranked<T>(IEnumerable<T> items, Func<T, NameMatch?> rank) =>
        items
            .Select(item => (Item: item, Rank: rank(item)))
            .Where(x => x.Rank is not null)
            .OrderBy(x => x.Rank) // stable: list order within a rank
            .Select(x => x.Item)
            .ToList();
}
