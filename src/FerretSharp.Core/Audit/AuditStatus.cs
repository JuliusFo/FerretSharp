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

/// <param name="Reasons">Why it is outdated, short, for the user; empty otherwise.</param>
public sealed record AuditStatus(AuditHealth Health, AuditNames Names, IReadOnlyList<string> Reasons)
{
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
        if (state.AuditTable is not { } audit)
        {
            reasons.Add(TextFormat.Format(AuditText.StatusTableMissing, names.AuditTable.Name));
        }
        else
        {
            var has = audit.Columns.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
            if (template.TableFor(table, columns).Columns.Where(c => !has.Contains(c.Name)).Select(c => c.Name).ToList() is { Count: > 0 } missing)
            {
                reasons.Add(TextFormat.Format(AuditText.StatusColumnsMissing, string.Join(", ", missing)));
            }
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
        // fit): the trigger may already make DML fail – never "up to date" (R5 review). A trigger clash is named above.
        var plan = AuditPlanner.Plan(template, table, columns, state, DataFacts.None);
        if (state.Trigger is not { Table: var other } || other == table)
        {
            reasons.AddRange(plan.Problems.Where(p => !reasons.Contains(p)));
        }

        if (reasons.Count == 0 && plan.Steps.Count > 0)
        {
            reasons.Add(AuditText.StatusOtherDifferences); // types, comments, a column the table no longer has
        }

        return new AuditStatus(reasons.Count == 0 ? AuditHealth.UpToDate : AuditHealth.Outdated, names, reasons);
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
