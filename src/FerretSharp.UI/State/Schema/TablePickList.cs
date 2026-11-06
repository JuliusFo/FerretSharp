using FerretSharp.Core.Design;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;

namespace FerretSharp.UI.State;

/// <summary>A table to pick, with its primary key (empty without one) and why it is suggested (null: not suggested).</summary>
public sealed record TablePickRow(TableSummary Table, IReadOnlyList<string> Key, ForeignKeyTargetReason? Reason = null);

/// <summary>
/// The lines of the table picker (the designer's foreign key target): the suggested tables first, then the rest – every table
/// in list order without a query, the matches ranked like the quick open with one (<see cref="ObjectSearch.FindTables"/>,
/// also by C# entity name). At most <see cref="Limit"/> lines of the rest; <see cref="More"/> says how many are left out.
/// </summary>
public sealed record TablePickList(IReadOnlyList<TablePickRow> Suggested, IReadOnlyList<TablePickRow> Rest, int More)
{
    /// <summary>Lines of the rest shown at most: an unfiltered schema has thousands, and typing narrows faster than scrolling.</summary>
    public const int Limit = 100;

    /// <summary>The line Enter in the search field picks: the first one; null if nothing matches.</summary>
    public TablePickRow? First => Suggested.Count > 0 ? Suggested[0] : Rest.Count > 0 ? Rest[0] : null;

    /// <param name="tables">The tables to choose from, in list order.</param>
    /// <param name="query">What is typed in the search field.</param>
    /// <param name="suggested">The suggested targets, most certain first; ones not in <paramref name="tables"/> are left out.</param>
    /// <param name="keyOf">The primary key columns of a table.</param>
    /// <param name="entityName">The C# entity name of a table; null if it has none.</param>
    public static TablePickList Of(
        IReadOnlyList<TableSummary> tables, string? query, IReadOnlyList<ForeignKeyTarget> suggested, Func<TableRef, IReadOnlyList<string>> keyOf,
        Func<TableRef, string?>? entityName = null)
    {
        ArgumentNullException.ThrowIfNull(tables);
        ArgumentNullException.ThrowIfNull(suggested);
        ArgumentNullException.ThrowIfNull(keyOf);
        var matching = NameRank.Terms(query).Length == 0 ? tables : ObjectSearch.FindTables(tables, query, entityName);
        var byRef = matching.DistinctBy(t => t.Ref).ToDictionary(t => t.Ref);
        var top = suggested.Where(s => byRef.ContainsKey(s.Table))
            .Select(s => new TablePickRow(byRef[s.Table], s.Key, s.Reason))
            .ToList();
        var shown = top.Select(r => r.Table.Ref).ToHashSet();
        var rest = matching.Where(t => !shown.Contains(t.Ref)).ToList();
        return new TablePickList(
            top,
            rest.Take(Limit).Select(t => new TablePickRow(t, keyOf(t.Ref))).ToList(),
            Math.Max(0, rest.Count - Limit));
    }
}
