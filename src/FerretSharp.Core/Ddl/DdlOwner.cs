using System.Text;
using FerretSharp.Core.Query;

namespace FerretSharp.Core.Ddl;

/// <summary>
/// DDL without the object's own schema ("Without owner" in the DDL view): for running it under another owner.
/// <c>DBMS_METADATA</c> can leave the schema out only through <c>SET_TRANSFORM_PARAM</c> – PL/SQL, which the read path
/// refuses (ADR 0006) – so the text it returned loses it afterwards.
/// </summary>
public static class DdlOwner
{
    /// <summary>
    /// <paramref name="ddl"/> without <c>"OWNER".</c> wherever the quoted <paramref name="owner"/> is directly followed by a
    /// dot, as <c>DBMS_METADATA</c> and <see cref="OracleDdl"/> write every name. String literals and comments stay as
    /// they are, and so do other schemas (a foreign key into another schema still needs it) and unquoted names in view or
    /// trigger source – <c>HR.X</c> there may as well be a table alias called HR.
    /// </summary>
    public static string Omit(string ddl, string owner)
    {
        ArgumentNullException.ThrowIfNull(ddl);
        ArgumentException.ThrowIfNullOrEmpty(owner);
        var tokens = SqlTokenizer.Tokenize(ddl);
        var text = new StringBuilder(ddl.Length);
        var copied = 0;
        for (var i = 0; i + 1 < tokens.Count; i++)
        {
            if (tokens[i] is { Kind: SqlTokenKind.QuotedIdentifier } name && string.Equals(name.Value, owner, StringComparison.Ordinal)
                && tokens[i + 1].IsSymbol("."))
            {
                text.Append(ddl, copied, name.Start - copied);
                copied = tokens[i + 1].End;
                i++;
            }
        }

        return copied == 0 ? ddl : text.Append(ddl, copied, ddl.Length - copied).ToString();
    }
}
