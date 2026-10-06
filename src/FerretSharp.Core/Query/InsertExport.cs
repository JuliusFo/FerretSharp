using System.Globalization;
using System.Text;
using FerretSharp.Core.Data;
using FerretSharp.Core.Oracle;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Query;

/// <summary>
/// Rows as an INSERT script – text only, FerretSharp never executes it (v1 has no write path). Values become Oracle
/// literals that reproduce them exactly; LOBs that were only loaded as preview become NULL with a comment and a warning.
/// </summary>
public static class InsertExport
{
    /// <summary>Comments are German like the rest of the UI, independent of the machine's culture.</summary>
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

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
            .Append("-- ").Append(rows.Count == 1 ? "1 Zeile" : $"{rows.Count} Zeilen").Append(" aus ").Append(target)
            .Append(" (FerretSharp-Export)\r\n");
        if (hasAmpersand)
        {
            header.Append("-- Enthält '&': in SQL*Plus vorher SET DEFINE OFF ausführen.\r\n");
        }

        foreach (var identity in table.Columns.Where(c => c.IsIdentity))
        {
            header.Append("-- ").Append(identity.Name)
                .Append(" ist eine Identity-Spalte: GENERATED ALWAYS lehnt explizite Werte ab (ORA-32795).\r\n");
        }

        return new ExportText(header.Append(statements).ToString(), rows.Count, warnings.ToList());
    }

    /// <summary>An Oracle literal for <paramref name="value"/> read from <paramref name="column"/>.</summary>
    internal static string Literal(ColumnInfo column, object? value, ExportWarnings warnings) => value switch
    {
        null or DBNull => "NULL",
        decimal d => d.ToString(CultureInfo.InvariantCulture),
        BigNumber big => big.Invariant,
        int or long or short => Convert.ToString(value, CultureInfo.InvariantCulture)!,
        double d => Binary("DOUBLE", double.IsNaN(d), double.IsInfinity(d) ? Math.Sign(d) : 0, d.ToString("R", CultureInfo.InvariantCulture) + "d"),
        float f => Binary("FLOAT", float.IsNaN(f), float.IsInfinity(f) ? Math.Sign(f) : 0, f.ToString("R", CultureInfo.InvariantCulture) + "f"),
        DateTimeOffset dto => $"TO_TIMESTAMP_TZ('{dto.ToString("yyyy-MM-dd HH:mm:ss.fffffff zzz", CultureInfo.InvariantCulture)}', 'YYYY-MM-DD HH24:MI:SS.FF7 TZH:TZM')",
        DateTime dt when ColumnCategories.Of(column) == ColumnCategory.Date =>
            $"TO_DATE('{dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}', 'YYYY-MM-DD HH24:MI:SS')",
        DateTime dt => $"TO_TIMESTAMP('{dt.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture)}', 'YYYY-MM-DD HH24:MI:SS.FF7')",
        bool b => b ? "TRUE" : "FALSE",
        byte[] bytes => $"HEXTORAW('{Convert.ToHexString(bytes)}')",
        string s when ColumnCategories.Of(column) == ColumnCategory.Interval =>
            column.DataType.StartsWith("INTERVAL YEAR", StringComparison.Ordinal) ? $"TO_YMINTERVAL('{s}')" : $"TO_DSINTERVAL('{s}')",
        string s => Text(column, s),
        LobValue { Length: 0, Preview: null } => "EMPTY_BLOB()",
        LobValue { Length: 0 } => "EMPTY_CLOB()",
        LobValue { Preview: { } preview } clob when preview.Length == clob.Length => Text(column, preview),
        LobValue { Preview: null } blob => Skipped(column, warnings, blob.Length.ToString("N0", German) + " Bytes", "nur die Länge geladen"),
        LobValue clob => Skipped(column, warnings, clob.Length.ToString("N0", German) + " Zeichen", "nur die Vorschau geladen"),
        NotNullMarker marker => Skipped(column, warnings, marker.DataType, "Typ wird nicht exportiert"),
        _ => Skipped(column, warnings, value.GetType().Name, "Typ wird nicht exportiert"),
    };

    /// <summary>N'…' for national character columns, so characters outside the database charset survive.</summary>
    private static string Text(ColumnInfo column, string value) =>
        (column.DataType is "NVARCHAR2" or "NCHAR" or "NCLOB" ? "N'" : "'") + value.Replace("'", "''", StringComparison.Ordinal) + "'";

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
        return $"NULL /* {column.DataType} ({what}) nicht exportiert */";
    }
}
