using System.Data;
using FerretSharp.Core.Query;
using Oracle.ManagedDataAccess.Client;

namespace FerretSharp.Core.Oracle;

/// <summary>
/// FerretSharp's bind parameters as ODP.NET parameters: <see cref="QueryBuilder"/> and the other builders know nothing of
/// the driver (CLAUDE.md section 4), the mapping happens here, for <see cref="OracleSession"/>.
/// </summary>
internal static class OracleParameters
{
    /// <summary>Size of an output parameter (<c>RETURNING ROWID INTO</c>): a VARCHAR2 the length of the largest one.</summary>
    private const int OutputSize = 4000;

    public static OracleParameter From(QueryParameter parameter)
    {
        if (parameter.Output)
        {
            return new OracleParameter(parameter.Name, OracleDbType.Varchar2, OutputSize) { Direction = ParameterDirection.Output };
        }

        var result = new OracleParameter(parameter.Name, parameter.Value ?? DBNull.Value);
        if (DbTypeOf(parameter.Type) is { } type)
        {
            result.OracleDbType = type;
        }

        return result;
    }

    /// <summary>The values of the output parameters by name, as text.</summary>
    public static IReadOnlyDictionary<string, object?> Outputs(OracleCommand command) =>
        command.Parameters.Cast<OracleParameter>()
            .Where(p => p.Direction == ParameterDirection.Output)
            .ToDictionary(p => p.ParameterName, p => p.Value is DBNull or null ? null : (object?)p.Value.ToString());

    /// <summary>Null: let ODP.NET infer the type from the value.</summary>
    private static OracleDbType? DbTypeOf(OracleTypeHint hint) => hint switch
    {
        OracleTypeHint.NVarchar2 => OracleDbType.NVarchar2,
        OracleTypeHint.Varchar2 => OracleDbType.Varchar2,
        OracleTypeHint.Char => OracleDbType.Char,
        OracleTypeHint.Number => OracleDbType.Decimal,
        OracleTypeHint.Date => OracleDbType.Date,
        OracleTypeHint.TimeStamp => OracleDbType.TimeStamp,
        OracleTypeHint.Raw => OracleDbType.Raw,
        OracleTypeHint.Clob => OracleDbType.Clob,
        OracleTypeHint.NClob => OracleDbType.NClob,
        OracleTypeHint.Blob => OracleDbType.Blob,
        _ => null,
    };
}
