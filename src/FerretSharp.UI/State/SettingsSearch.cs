using System.Globalization;
using System.Text.RegularExpressions;

namespace FerretSharp.UI.State;

/// <summary>
/// The search of the settings page (WP-34): a setting matches if every word of the query is in its label, its hint or the
/// name of its section – case and accents ignored ("uber" finds "Über"), placeholders of the texts (<c>{0}</c>) left out.
/// </summary>
public sealed partial class SettingsSearch
{
    public static readonly SettingsSearch None = new("");

    private readonly string[] _words;

    public SettingsSearch(string? query)
    {
        Query = query?.Trim() ?? "";
        _words = Query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
    }

    public string Query { get; }

    /// <summary>A query was typed: the page shows what matches in every section instead of the selected section.</summary>
    public bool IsActive => _words.Length > 0;

    /// <summary>Every word of the query is in one of the texts; true without a query.</summary>
    public bool Matches(params string?[] texts)
    {
        if (!IsActive)
        {
            return true;
        }

        var text = Placeholder().Replace(string.Join(" ", texts.Where(t => t is not null)), " ");
        return _words.All(word => CultureInfo.InvariantCulture.CompareInfo.IndexOf(text, word, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0);
    }

    [GeneratedRegex(@"\{\d+\}")]
    private static partial Regex Placeholder();
}
