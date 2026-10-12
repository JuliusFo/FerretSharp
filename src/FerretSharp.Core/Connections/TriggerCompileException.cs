using FerretSharp.Core.Query;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Connections;

/// <summary>
/// A trigger was created through the schema path but does not compile (status INVALID; ODP.NET reports that as success,
/// WP-26, ADR 0021). It exists and is invalid; created by FerretSharp with <c>DISABLE</c>, it stays disabled, so DML on its table keeps working. Only a
/// trigger whose statement said <c>ENABLE</c> itself is enabled (<see cref="IsEnabled"/>) – then every DML on its table
/// fails with ORA-04098, and the message says so.
/// </summary>
public sealed class TriggerCompileException : DatabaseException
{
    /// <summary>Compile errors listed in the message; the rest is counted.</summary>
    private const int ListedErrors = 3;

    /// <summary>ORA-24344: success with compilation error.</summary>
    private const string CompiledWithErrors = "ORA-24344";

    /// <param name="errors">The trigger's errors from <c>ALL_ERRORS</c>; empty if they could not be read.</param>
    /// <param name="isEnabled">The statement itself said ENABLE: the invalid trigger fires.</param>
    /// <param name="statement">The CREATE that ran (with the DISABLE FerretSharp put in).</param>
    /// <param name="inner">
    /// ORA-24344 if the driver reported the compile error; null if it reported success – ODP.NET does so for triggers
    /// (Oracle 23), only the status of the trigger tells. The error code is ORA-24344 either way: that is what happened.
    /// </param>
    /// <param name="source">
    /// The statement as the user wrote it, for the positions in the message (the SQL editor marks them there); the one
    /// that ran by default. Without the DISABLE FerretSharp put in, a body on the header's line keeps its columns.
    /// </param>
    public TriggerCompileException(
        string owner, string name, IReadOnlyList<PlSqlError> errors, bool isEnabled, QuerySpec? statement, Exception? inner = null, string? source = null)
        : base(MessageOf(owner, name, errors, isEnabled, source ?? statement?.Sql), CompiledWithErrors, inner)
    {
        Owner = owner;
        Name = name;
        Errors = errors;
        IsEnabled = isEnabled;
        Statement = statement;
    }

    public string Owner { get; }

    public string Name { get; }

    public IReadOnlyList<PlSqlError> Errors { get; }

    /// <summary>The invalid trigger is enabled (the user wrote ENABLE); normally false.</summary>
    public bool IsEnabled { get; }

    /// <summary>
    /// "12:5 PLS-00201: …; 14:3 …" – the first errors as line:column and text, then how many more. With the statement the
    /// lines are its own (as the SQL editor numbers them); Oracle counts a trigger's lines from its body (WP-26).
    /// </summary>
    internal static string ListOf(IReadOnlyList<PlSqlError> errors, string? statement = null)
    {
        if (errors.Count == 0)
        {
            return OracleText.TriggerErrorsUnreadable;
        }

        var listed = string.Join("; ", errors.Take(ListedErrors).Select(e => $"{Position(e, statement)} {e.Text}"));
        var more = errors.Count - ListedErrors;
        return more > 0 ? $"{listed} {TextFormat.Plural(more, OracleText.TriggerMoreErrorsOne, OracleText.TriggerMoreErrorsOther)}" : listed;
    }

    /// <summary>Line:column of an error in the statement; Oracle's own if it cannot be placed there.</summary>
    private static string Position(PlSqlError error, string? statement)
    {
        if (statement is null || Ddl.TriggerDdl.ErrorOffset(statement, error.Line, error.Column) is not { } at)
        {
            return $"{error.Line}:{error.Column}";
        }

        var lineStart = at == 0 ? 0 : statement.LastIndexOf('\n', at - 1) + 1;
        var line = 1 + statement.AsSpan(0, lineStart).Count('\n');
        return $"{line}:{at - lineStart + 1}";
    }

    private static string MessageOf(string owner, string name, IReadOnlyList<PlSqlError> errors, bool isEnabled, string? statement) =>
        TextFormat.Format(isEnabled ? OracleText.TriggerCompileErrorsEnabled : OracleText.TriggerCompileErrorsDisabled, $"{owner}.{name}", ListOf(errors, statement));
}
