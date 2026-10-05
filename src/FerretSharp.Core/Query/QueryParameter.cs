namespace FerretSharp.Core.Query;

/// <summary>Optional Oracle type for a bind variable; <see cref="Auto"/> lets the driver infer it from the value.</summary>
public enum OracleTypeHint
{
    Auto,
    Varchar2,

    /// <summary>Blank-padded comparison semantics for CHAR columns ('AB' matches 'AB ').</summary>
    Char,
    Number,
    Date,
    TimeStamp,

    /// <summary>Binary (RAW columns); the value is a byte array.</summary>
    Raw,

    /// <summary>National character set (NVARCHAR2/NCHAR), so characters outside the database character set survive.</summary>
    NVarchar2,

    /// <summary>A ROWID returned by <c>RETURNING ROWID INTO</c> (output parameter).</summary>
    RowId,

    /// <summary>CLOB; the value is the whole text (LOB editor, WP-10).</summary>
    Clob,

    /// <summary>NCLOB (national character set); the value is the whole text.</summary>
    NClob,

    /// <summary>BLOB; the value is the whole content as a byte array.</summary>
    Blob,
}

/// <summary>Driver-neutral bind variable; mapped to <c>OracleParameter</c> by <c>OracleSession</c>.</summary>
/// <param name="Name">Name without the leading colon.</param>
/// <param name="Output">Filled by the statement (<c>RETURNING … INTO :name</c>); <see cref="Value"/> is ignored.</param>
public sealed record QueryParameter(string Name, object? Value, OracleTypeHint Type = OracleTypeHint.Auto, bool Output = false);
