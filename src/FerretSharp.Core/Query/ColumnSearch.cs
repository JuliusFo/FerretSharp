using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Query;

/// <summary>
/// Finds columns by name for the column picker (filter bar) and the column jump (Ctrl+F). Every whitespace-separated
/// term must occur in the name, ignoring case; underscores may be left out ("lieferort" finds LIEFER_ORT).
/// </summary>
public static class ColumnSearch
{
    private enum Rank
    {
        Exact,
        Prefix,
        WordStart,
        Contains,
    }

    /// <summary>
    /// Indexes into <paramref name="columns"/> of the matches: exact name first, then names starting with the first
    /// term, then names where every term starts a word (after "_"), then the rest – each group in schema order.
    /// An empty query returns all columns in schema order.
    /// </summary>
    public static IReadOnlyList<int> Find(IReadOnlyList<ColumnInfo> columns, string? query)
    {
        var terms = (query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0)
        {
            return Enumerable.Range(0, columns.Count).ToList();
        }

        return columns
            .Select((c, i) => (Index: i, Rank: RankOf(c.Name, terms)))
            .Where(x => x.Rank is not null)
            .OrderBy(x => x.Rank) // stable: schema order within a rank
            .Select(x => x.Index)
            .ToList();
    }

    private static Rank? RankOf(string name, string[] terms)
    {
        var compact = name.Replace("_", "", StringComparison.Ordinal);
        if (!terms.All(t => Contains(name, t) || Contains(compact, t)))
        {
            return null;
        }

        if (string.Equals(name, string.Join('_', terms), StringComparison.OrdinalIgnoreCase)
            || string.Equals(compact, string.Concat(terms), StringComparison.OrdinalIgnoreCase))
        {
            return Rank.Exact;
        }

        if (name.StartsWith(terms[0], StringComparison.OrdinalIgnoreCase) || compact.StartsWith(terms[0], StringComparison.OrdinalIgnoreCase))
        {
            return Rank.Prefix;
        }

        return terms.All(t => StartsWord(name, t)) ? Rank.WordStart : Rank.Contains;
    }

    private static bool Contains(string text, string term) => text.Contains(term, StringComparison.OrdinalIgnoreCase);

    /// <summary>The term occurs right after an underscore ("ort" in LIEFER_ORT, not in SORTE).</summary>
    private static bool StartsWord(string name, string term) =>
        name.Contains("_" + term, StringComparison.OrdinalIgnoreCase);
}
