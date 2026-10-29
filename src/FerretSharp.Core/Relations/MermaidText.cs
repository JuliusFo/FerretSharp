namespace FerretSharp.Core.Relations;

/// <summary>
/// Text from the database inside Mermaid (WP-46, WP-60): names and values come from tables, a quoted Oracle identifier may hold
/// a line break, a value anything. One rule for both diagrams (review 3.54.0, A10).
/// </summary>
public static class MermaidText
{
    /// <summary>
    /// On one line – a line break ended the label or comment and made the rest Mermaid lines of their own – and without
    /// <c>%%</c>, which starts a comment or a directive (<c>%%{init: …}%%</c>) wherever it stands.
    /// </summary>
    public static string Plain(string text)
    {
        var line = text.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\r', ' ').Replace('\n', ' ');
        while (line.Contains("%%", StringComparison.Ordinal))
        {
            line = line.Replace("%%", "% %", StringComparison.Ordinal);
        }

        return line;
    }
}
