using System.Text.RegularExpressions;
using FerretSharp.Core.Query;

namespace FerretSharp.UI.State;

/// <summary>
/// The search of the settings page (WP-34): a setting matches if every word of the query is in its label, its hint or the
/// name of its section – case and accents ignored ("uber" finds "Über"), placeholders of the texts (<c>{0}</c>) left out.
/// </summary>
public sealed partial class SettingsSearch(string? query)
{
    public static readonly SettingsSearch None = new("");

    private readonly TextSearch _search = new(query);

    public string Query => _search.Query;

    /// <summary>A query was typed: the page shows what matches in every section instead of the selected section.</summary>
    public bool IsActive => _search.IsActive;

    /// <summary>Every word of the query is in one of the texts (<see cref="TextSearch"/>); true without a query.</summary>
    public bool Matches(params string?[] texts) =>
        !IsActive || _search.Matches(Placeholder().Replace(string.Join(" ", texts.Where(t => t is not null)), " "));

    [GeneratedRegex(@"\{\d+\}")]
    private static partial Regex Placeholder();
}
