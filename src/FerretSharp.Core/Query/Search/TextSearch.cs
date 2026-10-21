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

    /// <summary>
    /// Where the words of the query occur in <paramref name="text"/>, for highlighting hits (WP-38): every occurrence of
    /// every word, compared as in <see cref="Matches"/>, sorted, overlapping or adjacent ones merged; none without a
    /// query. A word found only without the underscores of a name ("lieferort" in LIEFER_ORT) is not marked.
    /// </summary>
    public IReadOnlyList<TextRange> Ranges(string? text)
    {
        if (string.IsNullOrEmpty(text) || _words.Length == 0)
        {
            return [];
        }

        var found = new List<TextRange>();
        foreach (var word in _words)
        {
            var start = 0;
            while (start < text.Length)
            {
                var offset = Compare.IndexOf(text.AsSpan(start), word, Options, out var length);
                if (offset < 0 || length == 0)
                {
                    break;
                }

                found.Add(new TextRange(start + offset, length));
                start += offset + length;
            }
        }

        found.Sort((a, b) => a.Start.CompareTo(b.Start));
        var merged = new List<TextRange>();
        foreach (var range in found)
        {
            if (merged.Count > 0 && range.Start <= merged[^1].End)
            {
                var last = merged[^1];
                merged[^1] = last with { Length = Math.Max(last.End, range.End) - last.Start };
            }
            else
            {
                merged.Add(range);
            }
        }

        return merged;
    }

    public static bool Contains(string text, string word) => Compare.IndexOf(text, word, Options) >= 0;

    public static bool StartsWith(string text, string word) => Compare.IsPrefix(text, word, Options);

    public static bool Equal(string a, string b) => Compare.Compare(a, b, Options) == 0;
}

/// <summary>A part of a text: where it starts and how long it is.</summary>
public readonly record struct TextRange(int Start, int Length)
{
    public int End => Start + Length;
}
