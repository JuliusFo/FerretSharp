namespace FerretSharp.Core.Oracle;

/// <summary>Text as an SQL literal – for statements FerretSharp shows or writes into scripts, never for values it runs (those are binds).</summary>
public static class OracleLiteral
{
    /// <summary><c>'O''Brien'</c>; <c>N'…'</c> in the national character set (NCHAR, NVARCHAR2, NCLOB). Null is <c>''</c> (Oracle: NULL).</summary>
    public static string Text(string? text, bool national = false) => (national ? "N'" : "'") + Escape(text ?? "") + "'";

    /// <summary>The text between the quotes of a literal: each <c>'</c> doubled.</summary>
    public static string Escape(string text) => text.Replace("'", "''", StringComparison.Ordinal);
}
