using System.Globalization;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Data;

/// <summary>
/// How a value is written as text when exporting (R6b, review 3.40.0, B6): German notation without grouping, the full
/// value – unlike the grid's display text. The CSV and clipboard export write every value like this, the Excel export
/// everything it does not write as number, date or bool.
/// </summary>
internal static class ExportNotation
{
    private static readonly CultureInfo German = ValueCulture.German;

    /// <summary>The value as text; NULL is empty. Null if the value cannot be exported (counted in <paramref name="warnings"/>).</summary>
    public static string? Text(ColumnInfo column, object? value, ExportWarnings warnings) => value switch
    {
        null or DBNull => "",
        decimal d => d.ToString(German),
        BigNumber big => big.Invariant.Replace('.', ','),
        int or long or short => Convert.ToString(value, CultureInfo.InvariantCulture)!,
        double d => d.ToString("R", German),
        float f => f.ToString("R", German),
        DateTimeOffset dto => dto.ToString("dd.MM.yyyy HH:mm:ss.FFFFFFF zzz", CultureInfo.InvariantCulture),
        DateTime dt when ColumnCategories.Of(column) == ColumnCategory.Date => dt.ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture),
        DateTime dt => dt.ToString("dd.MM.yyyy HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
        bool b => b ? "TRUE" : "FALSE",
        byte[] bytes => Convert.ToHexString(bytes),
        LobValue { Preview: { } preview } clob when preview.Length == clob.Length => preview,
        LobValue lob => warnings.Skip(column, lob.Preview is null ? DataText.ExportReasonOnlyLength : DataText.ExportReasonOnlyPreview),
        NotNullMarker => warnings.Skip(column, DataText.ExportReasonTypeNotExported),
        string s => s,
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
    };
}

/// <summary>
/// Collects skipped values per column, e.g. "NOTE (CLOB): 3 values not exported – only the preview was loaded.", and for
/// the Excel export shortened values and notes on the whole file.
/// </summary>
internal sealed class ExportWarnings
{
    private readonly Dictionary<(string Column, string Type, string Reason), int> _skipped = [];
    private readonly Dictionary<(string Column, string Type), int> _truncated = [];
    private readonly List<string> _notes = [];

    /// <returns>Null: the value is not exported.</returns>
    public string? Skip(ColumnInfo column, string reason)
    {
        var key = (column.Name, column.DataType, reason);
        _skipped[key] = _skipped.GetValueOrDefault(key) + 1;
        return null;
    }

    /// <summary>A value was cut to what an Excel cell holds.</summary>
    public void Truncate(ColumnInfo column)
    {
        var key = (column.Name, column.DataType);
        _truncated[key] = _truncated.GetValueOrDefault(key) + 1;
    }

    /// <summary>A warning about the whole export (rows left out).</summary>
    public void Note(string text) => _notes.Add(text);

    public List<string> ToList() =>
    [
        .. _skipped.Select(kv => TextFormat.Plural(kv.Value, DataText.ExportSkippedOne, DataText.ExportSkippedOther, kv.Key.Column, kv.Key.Type, kv.Key.Reason)),
        .. _truncated.Select(kv => TextFormat.Plural(kv.Value, DataText.ExportTruncatedOne, DataText.ExportTruncatedOther, kv.Key.Column, kv.Key.Type)),
        .. _notes,
    ];
}
