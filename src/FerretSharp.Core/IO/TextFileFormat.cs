namespace FerretSharp.Core.IO;

/// <summary>
/// The format a text file in a folder of the user's keeps when it is written again (SQL scripts, WP-77; C# files, WP-41):
/// one rule for both (review 3.70.0, B4).
/// </summary>
public static class TextFileFormat
{
    /// <summary>
    /// The line ending most lines end with; <c>\r\n</c> on a tie and for a file without any. One CRLF line pasted into an
    /// LF script made it all CRLF, and <c>git diff</c> showed the whole file (review 3.70.0, A23).
    /// </summary>
    public static string LineBreakOf(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var (crlf, lf) = (0, 0);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                if (i > 0 && text[i - 1] == '\r')
                {
                    crlf++;
                }
                else
                {
                    lf++;
                }
            }
        }

        return lf > crlf ? "\n" : "\r\n";
    }
}
