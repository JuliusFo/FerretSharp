namespace FerretSharp.Core.Query;

/// <summary>
/// Text as an SQL line comment: every physical line starts with <c>-- </c>. A quoted name or a value may hold a line break,
/// and the rest was an executable line in a script pasted into another connection (review of R8: the INSERT script's header
/// and the bind values of "Copy as SQL", as <c>DdlScript.Render</c> since review 3.54.0, A11).
/// </summary>
public static class SqlComment
{
    public static string Of(string text, string newLine) =>
        "-- " + string.Join(newLine + "-- ", text.ReplaceLineEndings("\n").Split('\n'));
}
