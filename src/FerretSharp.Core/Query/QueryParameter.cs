namespace FerretSharp.Core.Query;

/// <summary>Optional Oracle type for a bind variable; <see cref="Auto"/> lets the driver infer it from the value.</summary>
public enum OracleTypeHint
{
    Auto,
    Varchar2,
    Number,
    Date,
    TimeStamp,
}

/// <summary>Driver-neutral bind variable; mapped to <c>OracleParameter</c> by <c>OracleSession</c>.</summary>
/// <param name="Name">Name without the leading colon.</param>
public sealed record QueryParameter(string Name, object? Value, OracleTypeHint Type = OracleTypeHint.Auto);
