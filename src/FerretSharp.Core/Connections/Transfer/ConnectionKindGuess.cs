namespace FerretSharp.Core.Connections.Transfer;

/// <summary>
/// The environment of an imported connection whose source does not say (SQL Developer, own DBeaver types), guessed from its
/// name (WP-35). Production wins over test over development: a name with both is treated as the more dangerous one.
/// </summary>
public static class ConnectionKindGuess
{
    private static readonly string[] ProdWords = ["prod", "prd", "live"];
    private static readonly string[] TestWords = ["test", "tst", "qs", "qa"];
    private static readonly string[] DevWords = ["dev", "develop", "entw"];

    /// <returns>The guessed kind, <see cref="ConnectionKind.Other"/> if the name gives nothing away.</returns>
    public static ConnectionKind FromName(string? name)
    {
        var words = Words(name);
        if (Matches(words, ProdWords))
        {
            return ConnectionKind.Prod;
        }

        if (Matches(words, TestWords))
        {
            return ConnectionKind.Test;
        }

        return Matches(words, DevWords) ? ConnectionKind.Dev : ConnectionKind.Other;
    }

    /// <summary>
    /// A word matches a short key word (<c>qs</c>, <c>prd</c>) only as a whole and a longer one also as its start
    /// (<c>Produktion</c>, <c>Testsystem</c>, <c>Development</c>), so <c>ERPQS</c> stays unmatched rather than a
    /// guess from any two letters.
    /// </summary>
    private static bool Matches(IReadOnlyList<string> words, string[] keys) =>
        words.Any(word => keys.Any(key => key.Length <= 3 ? word == key : word.StartsWith(key, StringComparison.Ordinal)));

    /// <summary>Letters only, split at everything else (<c>ERP_Test-2</c> → <c>erp</c>, <c>test</c>).</summary>
    private static List<string> Words(string? name)
    {
        var words = new List<string>();
        var current = new System.Text.StringBuilder();
        foreach (var c in name ?? "")
        {
            if (char.IsLetter(c))
            {
                current.Append(char.ToLowerInvariant(c));
            }
            else if (current.Length > 0)
            {
                words.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            words.Add(current.ToString());
        }

        return words;
    }
}
