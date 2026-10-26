using FerretSharp.Core.Resources;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Audit;

/// <summary>How an existing audit column takes the values the trigger writes into it (<see cref="OracleTypes.FitOf"/>).</summary>
public enum AuditColumnFit
{
    /// <summary>A column of the template's own is wider than the template says: probably another template's audit table.</summary>
    WiderThanTemplate,

    /// <summary>The values go in with a loss (fractional seconds, time zone).</summary>
    Lossy,

    /// <summary>Shorter than the values can be: a longer value makes the DML on the table fail.</summary>
    TooShort,

    /// <summary>Another type: the trigger makes the DML on the table fail.</summary>
    Incompatible,
}

/// <summary>
/// An existing audit column that does not take every value of the table (or the template) whole – as data, so that the
/// status line can group many of them (a table whose audit table was not kept up for long) and say the advice once.
/// </summary>
/// <param name="Column">The audit column.</param>
/// <param name="OfTemplate">A column of the template's own, not a copy of a column of the table.</param>
/// <param name="AuditType">Its type in the audit table, as shown (<see cref="OracleTypes.DisplayType"/>).</param>
/// <param name="ValueType">The type of the values: the table's column, or the template's.</param>
public sealed record AuditColumnIssue(string Column, AuditColumnFit Fit, bool OfTemplate, string AuditType, string ValueType)
{
    /// <summary>The trigger may make DML on the table fail: the plan does not run, the status is never "up to date".</summary>
    public bool IsProblem => Fit is AuditColumnFit.TooShort or AuditColumnFit.Incompatible;

    /// <summary>The whole sentence, with the advice (designer).</summary>
    public string Message(string auditTable) => TextFormat.Format(
        Fit switch
        {
            AuditColumnFit.WiderThanTemplate => AuditText.PlanWiderThanTemplate,
            AuditColumnFit.Lossy => OfTemplate ? AuditText.PlanTypeLossyTemplate : AuditText.PlanTypeLossy,
            AuditColumnFit.TooShort => OfTemplate ? AuditText.PlanTypeTooShortTemplate : AuditText.PlanTypeTooShort,
            _ => OfTemplate ? AuditText.PlanTypeIncompatibleTemplate : AuditText.PlanTypeIncompatible,
        },
        Column, auditTable, AuditType, ValueType);
}
