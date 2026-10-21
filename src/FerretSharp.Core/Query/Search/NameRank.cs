namespace FerretSharp.Core.Query;

/// <summary>How well a name matches a query; lower is better.</summary>
public enum NameMatch
{
    Exact,
    Prefix,
    WordStart,
    Contains,
}

/// <summary>
/// The ranking of the name searches (column search, WP-38 quick open): every whitespace-separated term must occur in the
/// name, compared like every search box (<see cref="TextSearch"/>); underscores may be left out ("lieferort" finds
/// LIEFER_ORT). Exact name first, then names starting with the first term, then names where every term starts a word
/// (after "_"), then the rest.
/// </summary>
public static class NameRank
{
    /// <summary>The terms of a query, the words of every search box (<see cref="TextSearch.Words"/>); empty if nothing was typed.</summary>
    public static string[] Terms(string? query) => [.. new TextSearch(query).Words];

    /// <summary>How a database name matches; null if a term is missing. Without terms every name matches alike.</summary>
    public static NameMatch? Of(string name, IReadOnlyList<string> terms)
    {
        if (terms.Count == 0)
        {
            return NameMatch.Contains;
        }

        var compact = name.Replace("_", "", StringComparison.Ordinal);
        if (!terms.All(t => TextSearch.Contains(name, t) || TextSearch.Contains(compact, t)))
        {
            return null;
        }

        if (TextSearch.Equal(name, string.Join('_', terms)) || TextSearch.Equal(compact, string.Concat(terms)))
        {
            return NameMatch.Exact;
        }

        if (TextSearch.StartsWith(name, terms[0]) || TextSearch.StartsWith(compact, terms[0]))
        {
            return NameMatch.Prefix;
        }

        return terms.All(t => StartsWord(name, t)) ? NameMatch.WordStart : NameMatch.Contains;
    }

    /// <summary>How a C# name matches (WP-12: <c>KundeId</c> is found by "kundeid", and "id" starts a word); null if a term is missing.</summary>
    public static NameMatch? OfClrName(string name, IReadOnlyList<string> terms) => Of(Words(name), terms);

    /// <summary>The better of two matches; null if neither matches.</summary>
    public static NameMatch? Best(NameMatch? a, NameMatch? b) => a is null ? b : b is null ? a : (NameMatch)Math.Min((int)a, (int)b);

    /// <summary><c>KundeId</c> → <c>Kunde_Id</c>: C# word boundaries become underscores, as in database names.</summary>
    private static string Words(string name)
    {
        var text = new System.Text.StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]) && (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1])))
            {
                text.Append('_');
            }

            text.Append(name[i]);
        }

        return text.ToString();
    }

    /// <summary>The term occurs right after an underscore ("ort" in LIEFER_ORT, not in SORTE).</summary>
    private static bool StartsWord(string name, string term) => TextSearch.Contains(name, "_" + term);
}
