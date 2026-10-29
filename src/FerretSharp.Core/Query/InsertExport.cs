using System.Globalization;
using System.Text;
using FerretSharp.Core.Data;
using FerretSharp.Core.Oracle;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Query;

/// <summary>
/// Rows as an INSERT script – text only, FerretSharp never executes it (v1 has no write path). Values become Oracle
/// literals that reproduce them as read: exactly, except a TIMESTAMP(8/9) to 100 ns (warned) and a time zone region as its
/// offset. LOBs that were only loaded as preview become NULL with a comment and a warning.
/// </summary>
public static class InsertExport
{
    public static ExportText Build(TableDetails table, IReadOnlyList<RowData> rows)
    {
        var warnings = new ExportWarnings();
        var target = OracleIdentifier.Qualify(table.Table.Owner, table.Table.Name);
        var columns = string.Join(", ", table.Columns.Select(c => OracleIdentifier.Quote(c.Name)));
        var statements = new StringBuilder();
        var hasAmpersand = false;
        foreach (var row in rows)
        {
            var values = table.Columns.Select((c, i) => Literal(c, row.Values[i], warnings)).ToList();
            hasAmpersand |= values.Any(v => v.Contains('&', StringComparison.Ordinal));
            statements.Append("INSERT INTO ").Append(target).Append(" (").Append(columns).Append(") VALUES (")
                .AppendJoin(", ", values).Append(");\r\n");
        }

        var header = new StringBuilder()
            .Append(Comment(TextFormat.Plural(rows.Count, QueryText.InsertExportHeaderOne, QueryText.InsertExportHeaderOther, target)))
            .Append("\r\n");
        if (hasAmpersand)
        {
            header.Append("-- ").Append(QueryText.InsertExportAmpersand).Append("\r\n");
        }

        foreach (var identity in table.Columns.Where(c => c.IsIdentity))
        {
            header.Append(Comment(TextFormat.Format(QueryText.InsertExportIdentity, identity.Name))).Append("\r\n");
        }

        return new ExportText(header.Append(statements).ToString(), rows.Count, warnings.ToList());
    }

    /// <summary>
    /// A comment of one line per physical line: a quoted name may hold a line break, and the rest of it was an executable line
    /// in the script pasted into another connection (review of R8, as <c>DdlScript.Render</c> since review 3.54.0, A11).
    /// </summary>
    private static string Comment(string text) => "-- " + string.Join("\r\n-- ", text.ReplaceLineEndings("\n").Split('\n'));

    /// <summary>An Oracle literal for <paramref name="value"/> read from <paramref name="column"/>.</summary>
    internal static string Literal(ColumnInfo column, object? value, ExportWarnings warnings) => value switch
    {
        null or DBNull => "NULL",
        decimal d => d.ToString(CultureInfo.InvariantCulture),
        BigNumber big => big.Invariant,
        int or long or short => Convert.ToString(value, CultureInfo.InvariantCulture)!,
        double d => Binary("DOUBLE", double.IsNaN(d), double.IsInfinity(d) ? Math.Sign(d) : 0, d.ToString("R", CultureInfo.InvariantCulture) + "d"),
        float f => Binary("FLOAT", float.IsNaN(f), float.IsInfinity(f) ? Math.Sign(f) : 0, f.ToString("R", CultureInfo.InvariantCulture) + "f"),
        DateTimeOffset dto => warnings.Read(column, $"TO_TIMESTAMP_TZ('{dto.ToString("yyyy-MM-dd HH:mm:ss.fffffff zzz", CultureInfo.InvariantCulture)}', 'YYYY-MM-DD HH24:MI:SS.FF7 TZH:TZM')"),
        DateTime dt when ColumnCategories.Of(column) == ColumnCategory.Date =>
            $"TO_DATE('{dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}', 'YYYY-MM-DD HH24:MI:SS')",
        DateTime dt => warnings.Read(column, $"TO_TIMESTAMP('{dt.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture)}', 'YYYY-MM-DD HH24:MI:SS.FF7')"),
        bool b => b ? "TRUE" : "FALSE",
        byte[] bytes => $"HEXTORAW('{Convert.ToHexString(bytes)}')",
        string s when ColumnCategories.Of(column) == ColumnCategory.Interval =>
            OracleTypes.IsYearToMonth(column.DataType) ? $"TO_YMINTERVAL('{s}')" : $"TO_DSINTERVAL('{s}')",
        string s => Text(column, s),
        LobValue { Length: 0, Preview: null } => "EMPTY_BLOB()",
        LobValue { Length: 0 } => "EMPTY_CLOB()",
        LobValue { Preview: { } preview } clob when preview.Length == clob.Length => Text(column, preview),
        LobValue { Preview: null } blob => Skipped(column, warnings, LobContent.ByteCount(blob.Length), DataText.ExportReasonOnlyLength),
        LobValue clob => Skipped(column, warnings, LobContent.CharacterCount(clob.Length), DataText.ExportReasonOnlyPreview),
        NotNullMarker marker => Skipped(column, warnings, marker.DataType, DataText.ExportReasonTypeNotExported),
        _ => Skipped(column, warnings, value.GetType().Name, DataText.ExportReasonTypeNotExported),
    };

    /// <summary>
    /// N'…' for national character columns. Whether characters outside the database charset survive depends on the client
    /// that runs the script: Oracle converts the statement text to the database charset first, unless the client replaces
    /// N literals (ORA_NCHAR_LITERAL_REPLACE) – review 3.44.0, A18. The script is text for the user; FerretSharp binds values.
    /// </summary>
    private static string Text(ColumnInfo column, string value) => OracleLiteral.Text(value, OracleTypes.IsNational(column.DataType));

    /// <param name="infinity">+1/-1 for ±infinity, otherwise 0.</param>
    private static string Binary(string type, bool nan, int infinity, string literal) => (nan, infinity) switch
    {
        (true, _) => $"BINARY_{type}_NAN",
        (_, > 0) => $"BINARY_{type}_INFINITY",
        (_, < 0) => $"-BINARY_{type}_INFINITY",
        _ => literal,
    };

    private static string Skipped(ColumnInfo column, ExportWarnings warnings, string what, string reason)
    {
        warnings.Skip(column, reason);
        return "NULL /* " + TextFormat.Format(QueryText.InsertExportSkipped, column.DataType, what) + " */";
    }
}
