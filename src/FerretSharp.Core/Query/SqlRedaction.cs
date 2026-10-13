using System.Text;

namespace FerretSharp.Core.Query;

/// <summary>
/// Passwords in a statement's text – <c>CREATE/ALTER USER … IDENTIFIED BY</c>, a database link's
/// <c>CONNECT TO … IDENTIFIED BY</c> – masked before the text goes into the log or the SQL history (review 3.31.0, A10).
/// Bind values are masked elsewhere (<see cref="BindValues"/>); this is about the text itself.
/// </summary>
public static class SqlRedaction
{
    public const string Mask = "***";

    /// <summary>
    /// The text with what follows <c>IDENTIFIED BY</c> masked – also <c>IDENTIFIED BY VALUES '…'</c> and the old password
    /// after <c>REPLACE</c>; unchanged if there is none.
    /// </summary>
    public static string Redact(string sql)
    {
        if (sql.IndexOf("IDENTIFIED", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return sql;
        }

        var tokens = SqlTokenizer.Significant(sql);
        var secrets = new List<SqlToken>();
        for (var i = 0; i + 2 < tokens.Count; i++)
        {
            if (!tokens[i].IsWord("IDENTIFIED") || !tokens[i + 1].IsWord("BY"))
            {
                continue;
            }

            var at = tokens[i + 2].IsWord("VALUES") && i + 3 < tokens.Count ? i + 3 : i + 2;
            secrets.Add(tokens[at]);
            if (at + 2 < tokens.Count && tokens[at + 1].IsWord("REPLACE"))
            {
                secrets.Add(tokens[at + 2]);
            }
        }

        var text = new StringBuilder(sql);
        foreach (var secret in secrets.OrderByDescending(t => t.Start))
        {
            text.Remove(secret.Start, secret.Length).Insert(secret.Start, Mask);
        }

        return text.ToString();
    }
}
