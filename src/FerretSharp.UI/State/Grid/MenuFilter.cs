namespace FerretSharp.UI.State;

/// <summary>
/// What is typed into the filter field of a right-click menu (WP-61): an entry stays if each word of the text is part of one
/// of its labels (an action's name, a format, a table), ignoring case. Without text everything stays.
/// </summary>
public sealed class MenuFilter
{
    private string[] _words = [];

    public string Text { get; private set; } = "";

    /// <summary>Something is typed: folded lists open, empty groups disappear.</summary>
    public bool Active => _words.Length > 0;

    public void Set(string text)
    {
        Text = text;
        _words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
    }

    public bool Matches(params string?[] labels) =>
        _words.All(word => labels.Any(label => label?.Contains(word, StringComparison.CurrentCultureIgnoreCase) == true));
}
