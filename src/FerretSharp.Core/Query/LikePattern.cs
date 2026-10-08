namespace FerretSharp.Core.Query;

/// <summary>LIKE patterns with <c>ESCAPE '\'</c>: the text's own %, _ and \ match only themselves.</summary>
public static class LikePattern
{
    public static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    /// <summary>Matches any text containing <paramref name="value"/>.</summary>
    public static string Contains(string value) => "%" + Escape(value) + "%";
}
