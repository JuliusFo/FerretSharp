using System.Globalization;
using FerretSharp.Core.Query;

namespace FerretSharp.Core.ClrModel;

/// <summary>What FerretSharp does with a command the LINQ console captured.</summary>
public enum LinqCommandKind
{
    /// <summary>A query: read in the workspace's session (also aggregates like COUNT).</summary>
    Query,

    /// <summary>An UPDATE or DELETE (<c>ExecuteUpdate</c>/<c>ExecuteDelete</c>): only on writable workspaces, after confirmation.</summary>
    Write,

    /// <summary>Anything else (PL/SQL blocks of <c>SaveChanges</c> batches …): shown, never run.</summary>
    Unsupported,
}

/// <summary>Captured commands (ADR 0011) as FerretSharp statements: the SQL as EF wrote it, the parameters with their values.</summary>
public static class LinqStatements
{
    public static LinqCommandKind KindOf(CapturedCommand command) => command.Kind switch
    {
        // Same tokenizer as the SQL editor and the session guards: EF puts TagWith() comments before the statement.
        LinqProtocol.Reader or LinqProtocol.Scalar when SqlScript.Analyze(command.Sql).IsQuery => LinqCommandKind.Query,
        LinqProtocol.NonQuery when SqlScript.Analyze(command.Sql).Kind is SqlStatementKind.Update or SqlStatementKind.Delete => LinqCommandKind.Write,
        _ => LinqCommandKind.Unsupported,
    };

    public static QuerySpec ToQuery(CapturedCommand command) =>
        new(command.Sql.Trim(), command.Parameters.Select(ToParameter).ToList());

    public static QueryParameter ToParameter(CapturedParameter parameter) =>
        new(parameter.Name, ValueOf(parameter), TypeOf(parameter.OracleType));

    /// <summary>The value as the host saw it: invariant text back to its CLR type.</summary>
    internal static object? ValueOf(CapturedParameter parameter)
    {
        if (parameter.Value is not { } text)
        {
            return null;
        }

        var invariant = CultureInfo.InvariantCulture;
        return parameter.ClrType switch
        {
            "Int32" => int.Parse(text, invariant),
            "Int64" => long.Parse(text, invariant),
            "Int16" => short.Parse(text, invariant),
            "Byte" => byte.Parse(text, invariant),
            "SByte" => sbyte.Parse(text, invariant),
            "UInt16" => (int)ushort.Parse(text, invariant),
            "UInt32" => (long)uint.Parse(text, invariant),
            "UInt64" => decimal.Parse(text, invariant),
            "Decimal" => decimal.Parse(text, NumberStyles.Float, invariant),
            "Double" => double.Parse(text, NumberStyles.Float, invariant),
            "Single" => float.Parse(text, NumberStyles.Float, invariant),
            "Boolean" => bool.Parse(text),
            "DateTime" => DateTime.Parse(text, invariant, DateTimeStyles.RoundtripKind),
            "DateTimeOffset" => DateTimeOffset.Parse(text, invariant, DateTimeStyles.RoundtripKind),
            "TimeSpan" => TimeSpan.Parse(text, invariant),
            "Guid" => Guid.Parse(text),
            "Byte[]" => Convert.FromBase64String(text),
            _ => text,
        };
    }

    /// <summary>The provider's <c>OracleDbType</c> as a hint; numbers bind as NUMBER, binary floats by their value.</summary>
    internal static OracleTypeHint TypeOf(string? oracleType) => oracleType switch
    {
        "Varchar2" => OracleTypeHint.Varchar2,
        "NVarchar2" or "NChar" => OracleTypeHint.NVarchar2,
        "Char" => OracleTypeHint.Char,
        "Int16" or "Int32" or "Int64" or "Decimal" or "Byte" => OracleTypeHint.Number,
        "Date" => OracleTypeHint.Date,
        "TimeStamp" => OracleTypeHint.TimeStamp,
        "Raw" => OracleTypeHint.Raw,
        "Clob" => OracleTypeHint.Clob,
        "NClob" => OracleTypeHint.NClob,
        "Blob" => OracleTypeHint.Blob,
        _ => OracleTypeHint.Auto,
    };
}
