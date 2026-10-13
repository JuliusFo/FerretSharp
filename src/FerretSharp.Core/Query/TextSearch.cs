using System.Globalization;

namespace FerretSharp.Core.Query;

/// <summary>
/// The rule of every search box (R5b, review 3.31.0 B5; it was the settings search's, WP-34): a text matches if every
/// word of the query is in it – in any order, case and accents ignored ("uber" finds "Über", "kunde adr" finds
/// <c>KUNDE_ADRESSE</c>). The PL/SQL source search runs in the database and the column search ranks its hits; the latter
/// compares the same way (<see cref="Contains"/>, <see cref="StartsWith"/>, <see cref="Equal"/>).
/// </summary>
public sealed class TextSearch
{
    private const CompareOptions Options = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;

    private static readonly CompareInfo Compare = CultureInfo.InvariantCulture.CompareInfo;

    public static readonly TextSearch None = new("");

    private readonly string[] _words;

    public TextSearch(string? query)
    {
        Query = query?.Trim() ?? "";
        _words = Query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
    }

    public string Query { get; }

    /// <summary>The words of the query.</summary>
    public IReadOnlyList<string> Words => _words;

    /// <summary>A query was typed.</summary>
    public bool IsActive => _words.Length > 0;

    /// <summary>Every word of the query is in one of the texts; true without a query.</summary>
    public bool Matches(params ReadOnlySpan<string?> texts)
    {
        foreach (var word in _words)
        {
            var found = false;
            foreach (var text in texts)
            {
                if (text is not null && Contains(text, word))
                {
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                return false;
            }
        }

        return true;
    }

    public static bool Contains(string text, string word) => Compare.IndexOf(text, word, Options) >= 0;

    public static bool StartsWith(string text, string word) => Compare.IsPrefix(text, word, Options);

    public static bool Equal(string a, string b) => Compare.Compare(a, b, Options) == 0;
}
