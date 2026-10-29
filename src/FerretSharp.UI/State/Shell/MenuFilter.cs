using FerretSharp.Core.Query;

namespace FerretSharp.UI.State;

/// <summary>
/// What is typed into the filter field of a right-click menu (WP-61): an entry stays if each word of the text is part of one
/// of its labels (an action's name, a format, a table) – the rule of every search box, case and accents ignored
/// (<see cref="TextSearch"/>; review 3.54.0, A8: "anderung" did not find "Änderungen"). Without text everything stays.
/// </summary>
public sealed class MenuFilter
{
    private TextSearch _search = TextSearch.None;
    private string[] _terms = [];

    public string Text { get; private set; } = "";

    /// <summary>Something is typed: folded lists open, empty groups disappear.</summary>
    public bool Active => _search.IsActive;

    public void Set(string text)
    {
        Text = text;
        _search = new TextSearch(text);
        _terms = [.. _search.Words];
    }

    public bool Matches(params string?[] labels) => _search.Matches(labels);

    /// <summary>
    /// A database name as the palette and the explorer find it (<see cref="NameRank"/>): underscores may be left out –
    /// "kundeadresse" finds <c>KUNDE_ADRESSE</c>.
    /// </summary>
    public bool MatchesName(string name) => NameRank.Of(name, _terms) is not null;

    /// <summary>
    /// A group's heading stays while one of its lines matches – each line together with the heading, as the entries
    /// themselves are filtered (review 3.54.0, A22: "edit delete" kept "Delete row" without its heading).
    /// </summary>
    /// <param name="lines">The labels of its lines; null for one not offered.</param>
    public bool KeepsHeading(string heading, IEnumerable<string?> lines) => lines.Any(line => line is not null && Matches(line, heading));
}
