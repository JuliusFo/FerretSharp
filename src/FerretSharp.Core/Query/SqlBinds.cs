using System.Globalization;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Query;

/// <summary>How a variable of the SQL editor is bound.</summary>
public enum SqlVariableType
{
    Text,

    /// <summary>Text compared with blank padding, for CHAR columns ('AB' finds 'AB ').</summary>
    Char,
    Number,
    Date,

    /// <summary>Hex digits, bound as bytes (RAW columns, GUIDs).</summary>
    Raw,
    Null,
}

/// <summary>A bind variable of the SQL editor: its name (without colon), type and value as typed.</summary>
public sealed record SqlVariable(string Name, SqlVariableType Type, string Value);

/// <summary>A variable whose value does not fit its type, or one that is missing.</summary>
public sealed class SqlBindException(string message) : Exception(message);

/// <summary>
/// Bind variables of the free SQL editor (WP-17): values as typed (German or invariant numbers, German or ISO dates, as
/// in the filter bar) become parameters – never part of the statement text.
/// </summary>
public static class SqlBinds
{
    /// <summary>The statement with a parameter for each of its binds.</summary>
    /// <exception cref="SqlBindException">A bind has no variable, or its value does not fit the type.</exception>
    public static QuerySpec Bind(string sql, IReadOnlyList<string> binds, IReadOnlyList<SqlVariable> variables)
    {
        var parameters = new List<QueryParameter>(binds.Count);
        foreach (var bind in binds)
        {
            var variable = variables.FirstOrDefault(v => string.Equals(v.Name, bind, StringComparison.OrdinalIgnoreCase))
                           ?? throw new SqlBindException($"Für :{bind} fehlt ein Wert.");
            parameters.Add(ToParameter(bind, variable));
        }

        return new QuerySpec(sql, parameters);
    }

    private static QueryParameter ToParameter(string name, SqlVariable variable)
    {
        var text = variable.Value.Trim();
        switch (variable.Type)
        {
            case SqlVariableType.Null:
                return new QueryParameter(name, null, OracleTypeHint.Varchar2);
            case SqlVariableType.Text:
                return new QueryParameter(name, variable.Value, OracleTypeHint.Varchar2);
            case SqlVariableType.Char:
                return new QueryParameter(name, variable.Value, OracleTypeHint.Char);
            case SqlVariableType.Number when FilterRules.TryParseNumber(text, out var number):
                return new QueryParameter(name, number, OracleTypeHint.Number);
            case SqlVariableType.Number:
                throw new SqlBindException(text.Length == 0 ? $":{name}: Zahl fehlt (für NULL den Typ NULL wählen)." : $":{name}: „{text}“ ist keine Zahl.");
            case SqlVariableType.Date when FilterRules.TryParseDate(text, out var date, out _):
                return new QueryParameter(name, date, date.Ticks % TimeSpan.TicksPerSecond == 0 ? OracleTypeHint.Date : OracleTypeHint.TimeStamp);
            case SqlVariableType.Date:
                throw new SqlBindException(text.Length == 0 ? $":{name}: Datum fehlt (für NULL den Typ NULL wählen)." : $":{name}: „{text}“ ist kein Datum (TT.MM.JJJJ [hh:mm[:ss]] oder ISO).");
            case SqlVariableType.Raw when FilterRules.TryParseHex(text, out var bytes):
                return new QueryParameter(name, bytes, OracleTypeHint.Raw);
            case SqlVariableType.Raw:
                throw new SqlBindException($":{name}: „{text}“ ist kein Hex-Wert (z. B. CAFE01).");
            default:
                throw new SqlBindException($":{name}: unbekannter Typ.");
        }
    }

    /// <summary>
    /// The variables for a statement's binds, in their order: existing ones keep type and value, new ones get the type the
    /// compared column suggests (<paramref name="suggest"/>), else text. Variables of binds no longer used are kept at the
    /// end – the user may only have commented out a line.
    /// </summary>
    public static IReadOnlyList<SqlVariable> Reconcile(
        IReadOnlyList<string> binds, IReadOnlyList<SqlVariable> existing, Func<string, SqlVariableType?> suggest)
    {
        var result = new List<SqlVariable>();
        foreach (var bind in binds)
        {
            result.Add(existing.FirstOrDefault(v => string.Equals(v.Name, bind, StringComparison.OrdinalIgnoreCase))
                       ?? new SqlVariable(bind, suggest(bind) ?? SqlVariableType.Text, ""));
        }

        result.AddRange(existing.Where(v => !binds.Contains(v.Name, StringComparer.OrdinalIgnoreCase)));
        return result;
    }

    /// <summary>The variable type for comparing with a column.</summary>
    public static SqlVariableType TypeFor(ColumnInfo column) => ColumnCategories.Of(column) switch
    {
        ColumnCategory.Number => SqlVariableType.Number,
        ColumnCategory.Date or ColumnCategory.Timestamp or ColumnCategory.TimestampWithTimeZone => SqlVariableType.Date,
        ColumnCategory.Raw => SqlVariableType.Raw,
        ColumnCategory.Text when column.DataType is "CHAR" or "NCHAR" => SqlVariableType.Char,
        _ => SqlVariableType.Text,
    };

    /// <summary>A parameter of a generated statement as editor variable ("In SQL-Editor öffnen").</summary>
    public static SqlVariable FromParameter(QueryParameter parameter) => parameter.Value switch
    {
        null => new SqlVariable(parameter.Name, SqlVariableType.Null, ""),
        decimal or int or long or short => new SqlVariable(parameter.Name, SqlVariableType.Number,
            Convert.ToString(parameter.Value, CultureInfo.InvariantCulture)!),
        DateTime date => new SqlVariable(parameter.Name, SqlVariableType.Date,
            date.ToString(date.Ticks % TimeSpan.TicksPerSecond == 0 ? "yyyy-MM-dd HH:mm:ss" : "yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture)),
        byte[] bytes => new SqlVariable(parameter.Name, SqlVariableType.Raw, Convert.ToHexString(bytes)),
        string text => new SqlVariable(parameter.Name, parameter.Type == OracleTypeHint.Char ? SqlVariableType.Char : SqlVariableType.Text, text),
        _ => new SqlVariable(parameter.Name, SqlVariableType.Text, Convert.ToString(parameter.Value, CultureInfo.InvariantCulture) ?? ""),
    };
}
