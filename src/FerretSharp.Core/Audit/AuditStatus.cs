using FerretSharp.Core.Design;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Audit;

public enum AuditHealth
{
    /// <summary>No audit table and no trigger of the template's names – the normal case for most tables, no fault.</summary>
    None,
    UpToDate,

    /// <summary>Audit table or trigger do not match the table (<see cref="AuditStatus.Reasons"/>): changes are audited incompletely.</summary>
    Outdated,
}

/// <param name="Reasons">
/// Why it is outdated, short, for the user; empty otherwise. Missing columns and audit columns that cannot take the
/// values stand apart (<see cref="MissingColumns"/>, <see cref="ColumnIssues"/>): an audit table not kept up for long has
/// many of them, and the status line counts them instead of listing a sentence each.
/// </param>
public sealed record AuditStatus(AuditHealth Health, AuditNames Names, IReadOnlyList<string> Reasons)
{
    /// <summary>Columns of the table (and the template) the audit table lacks; "Bring up to date" adds them.</summary>
    public IReadOnlyList<string> MissingColumns { get; init; } = [];

    /// <summary>
    /// Audit columns that cannot take every value (<see cref="AuditColumnIssue.IsProblem"/>): the trigger may make DML on
    /// the table fail, and "Bring up to date" cannot change that – the audit column needs a change by hand.
    /// </summary>
    public IReadOnlyList<AuditColumnIssue> ColumnIssues { get; init; } = [];

    /// <summary>
    /// How the table's audit objects stand against the template (the columns view shows it, WP-26): none, up to date, or
    /// outdated with the reasons – the same comparison as the plan (<see cref="AuditPlanner.Plan"/>).
    /// </summary>
    public static AuditStatus Of(AuditTemplate template, TableRef table, IReadOnlyList<ColumnInfo> columns, AuditState state)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(state);
        var names = template.NamesFor(table);
        if (state is { AuditTable: null, Trigger: null })
        {
            return new AuditStatus(AuditHealth.None, names, []);
        }

        var reasons = new List<string>();
        IReadOnlyList<string> missing = [];
        if (state.AuditTable is not { } audit)
        {
            reasons.Add(TextFormat.Format(AuditText.StatusTableMissing, names.AuditTable.Name));
        }
        else
        {
            var has = audit.Columns.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
            missing = template.TableFor(table, columns).Columns.Where(c => !has.Contains(c.Name)).Select(c => c.Name).ToList();
        }

        var trigger = template.TriggerFor(table, columns);
        switch (state.Trigger)
        {
            case null:
                reasons.Add(TextFormat.Format(AuditText.StatusTriggerMissing, names.Trigger));
                break;
            case { Table: var on } when on != table:
                reasons.Add(TextFormat.Format(AuditText.StatusTriggerOnOtherTable, names.Trigger, on is null ? "–" : $"{on.Owner}.{on.Name}"));
                break;
            case { Valid: false }:
                reasons.Add(TextFormat.Format(AuditText.StatusTriggerInvalid, names.Trigger));
                break;
            case var existing when !existing.Columns.ToHashSet(StringComparer.Ordinal).SetEquals(trigger.Columns):
                reasons.Add(TextFormat.Format(AuditText.StatusTriggerOutdated, names.Trigger));
                break;
            case { Enabled: false }:
                reasons.Add(TextFormat.Format(AuditText.StatusTriggerDisabled, names.Trigger));
                break;
        }

        // What the plan cannot do (an audit column that cannot take the table's values, a template whose trigger does not
        // fit): the trigger may already make DML fail – never "up to date" (R5 review). A trigger clash is named above; the
        // column issues stand apart, the plan's sentences for them are left out here.
        var plan = AuditPlanner.Plan(template, table, columns, state, DataFacts.None);
        var issues = AuditPlanner.ColumnIssues(template, table, columns, state).Where(i => i.IsProblem).ToList();
        if (state.Trigger is not { Table: var other } || other == table)
        {
            var said = issues.Select(i => i.Message(names.AuditTable.Name)).ToHashSet(StringComparer.Ordinal);
            reasons.AddRange(plan.Problems.Where(p => !reasons.Contains(p) && !said.Contains(p)));
        }
        else
        {
            issues = [];
        }

        if (reasons.Count == 0 && missing.Count == 0 && issues.Count == 0 && plan.Steps.Count > 0)
        {
            reasons.Add(AuditText.StatusOtherDifferences); // types, comments, a column the table no longer has
        }

        var upToDate = reasons.Count == 0 && missing.Count == 0 && issues.Count == 0;
        return new AuditStatus(upToDate ? AuditHealth.UpToDate : AuditHealth.Outdated, names, reasons)
        {
            MissingColumns = missing,
            ColumnIssues = issues,
        };
    }

    /// <summary>
    /// The table is the audit table of another table of <paramref name="tables"/> (by the template's names): it gets no
    /// audit table of its own offered.
    /// </summary>
    public static bool IsAuditTable(AuditTemplate template, TableRef table, IEnumerable<TableRef> tables)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(tables);
        return tables.Any(other => other != table && template.NamesFor(other).AuditTable == table);
    }
}
