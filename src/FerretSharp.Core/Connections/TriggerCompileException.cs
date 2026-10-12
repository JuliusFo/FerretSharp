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
    public TriggerCompileException(
        string owner, string name, IReadOnlyList<PlSqlError> errors, bool isEnabled, QuerySpec? statement, Exception? inner = null)
        : base(MessageOf(owner, name, errors, isEnabled), CompiledWithErrors, inner)
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

    /// <summary>"12:5 PLS-00201: …; 14:3 …" – the first errors as line:column and text, then how many more.</summary>
    internal static string ListOf(IReadOnlyList<PlSqlError> errors)
    {
        if (errors.Count == 0)
        {
            return OracleText.TriggerErrorsUnreadable;
        }

        var listed = string.Join("; ", errors.Take(ListedErrors).Select(e => $"{e.Line}:{e.Column} {e.Text}"));
        var more = errors.Count - ListedErrors;
        return more > 0 ? $"{listed} {TextFormat.Plural(more, OracleText.TriggerMoreErrorsOne, OracleText.TriggerMoreErrorsOther)}" : listed;
    }

    private static string MessageOf(string owner, string name, IReadOnlyList<PlSqlError> errors, bool isEnabled) =>
        TextFormat.Format(isEnabled ? OracleText.TriggerCompileErrorsEnabled : OracleText.TriggerCompileErrorsDisabled, $"{owner}.{name}", ListOf(errors));
}
