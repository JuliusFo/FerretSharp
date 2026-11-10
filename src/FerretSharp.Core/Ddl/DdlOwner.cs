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
    /// dot and starts the name, as <c>DBMS_METADATA</c> and <see cref="OracleDdl"/> write every name: in
    /// <c>"SHOP"."SHOP"."NEXTVAL"</c> only the schema goes, the sequence SHOP stays, and so does <c>"OTHER"."SHOP"</c>.
    /// String literals and comments stay as they are, and so do other schemas (a foreign key into another schema still
    /// needs it) and unquoted names in view or trigger source – <c>HR.X</c> there may as well be a table alias called HR.
    /// </summary>
    public static string Omit(string ddl, string owner)
    {
        ArgumentNullException.ThrowIfNull(ddl);
        ArgumentException.ThrowIfNullOrEmpty(owner);
        var tokens = SqlTokenizer.Tokenize(ddl);
        var text = new StringBuilder(ddl.Length);
        var copied = 0;
        var afterDot = false;
        for (var i = 0; i + 1 < tokens.Count; i++)
        {
            var token = tokens[i];
            if (!afterDot && token.Kind == SqlTokenKind.QuotedIdentifier && string.Equals(token.Value, owner, StringComparison.Ordinal)
                && tokens[i + 1].IsSymbol("."))
            {
                text.Append(ddl, copied, token.Start - copied);
                copied = tokens[i + 1].End;
                i++;
                afterDot = true;
            }
            else if (token.Kind is not (SqlTokenKind.Space or SqlTokenKind.Comment))
            {
                afterDot = token.IsSymbol(".");
            }
        }

        return copied == 0 ? ddl : text.Append(ddl, copied, ddl.Length - copied).ToString();
    }
}
