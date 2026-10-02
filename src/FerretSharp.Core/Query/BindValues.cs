using System.Globalization;

namespace FerretSharp.Core.Query;

/// <summary>
/// Bind variables as readable text for error dialogs and logs. On Prod connections the values are masked (CLAUDE.md:
/// no bind values of Prod connections in logs) – only names and types remain.
/// </summary>
public static class BindValues
{
    public const string Masked = "‹maskiert›";

    public static string Format(QueryParameter parameter, bool mask) => mask ? Masked : parameter.Value switch
    {
        null => "NULL",
        string s => "'" + s.Replace("'", "''", StringComparison.Ordinal) + "'",
        byte[] bytes => $"HEXTORAW('{Convert.ToHexString(bytes)}')",
        DateTime dt => dt.TimeOfDay == TimeSpan.Zero
            ? dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : dt.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        var other => other.ToString() ?? "",
    };

    /// <summary>The statement followed by one line per bind variable, e.g. <c>-- :p0 = 'Müller'</c>.</summary>
    public static string Describe(QuerySpec statement, bool mask) =>
        statement.Parameters.Count == 0
            ? statement.Sql
            : statement.Sql + Environment.NewLine + string.Join(
                Environment.NewLine, statement.Parameters.Select(p => $"-- :{p.Name} = {Format(p, mask)}"));
}
