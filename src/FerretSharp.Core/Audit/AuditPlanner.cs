using FerretSharp.Core.Ddl;
using FerretSharp.Core.Design;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Audit;

/// <summary>A trigger of the template's name, as the dictionary has it.</summary>
/// <param name="Table">The table it is on; null for a trigger on a schema or the database.</param>
/// <param name="Columns">The columns of its table it references (<see cref="ISchemaReader.GetTriggerColumnsAsync"/>); empty while it is invalid.</param>
public sealed record AuditTriggerState(TableRef? Table, bool Valid, bool Enabled, IReadOnlyList<string> Columns);

/// <summary>What the database has of a table's audit objects.</summary>
/// <param name="AuditTable">The table of the template's audit table name; null if there is none.</param>
/// <param name="Trigger">The trigger of the template's trigger name; null if there is none.</param>
/// <param name="AuditTableComment">The existing audit table's comment (<see cref="ObjectInfo.Comment"/>).</param>
public sealed record AuditState(TableDetails? AuditTable, AuditTriggerState? Trigger, string? AuditTableComment = null)
{
    public static readonly AuditState None = new(null, null);
}

/// <summary>
/// The DDL that makes a table's audit table and trigger match the template (WP-26): creates both, or brings an existing
/// audit table up to date with the table's columns and rewrites an outdated trigger. The audit table keeps everything it
/// has (decision of the user 2026-10-12): columns the table no longer has stay (made nullable – the trigger no longer
/// fills them), types only grow, renames the designer knows of are followed. Built on the designer's writer
/// (<see cref="TableDesign"/>), so the same checks apply. Pure logic; what the database has comes from the caller.
/// </summary>
public static class AuditPlanner
{
    /// <summary>The counts the plan needs about the existing audit table (rows: may a new column be NOT NULL?).</summary>
    public static IReadOnlyList<DataQuestion> Questions(AuditTemplate template, TableRef table, IReadOnlyList<ColumnInfo> baseColumns, AuditState state,
        IReadOnlyDictionary<string, string>? renamed = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.AuditTable is not { } audit)
        {
            return [];
        }

        var draft = Draft(template, table, baseColumns, audit, DataFacts.None, renamed, [], [], [], []);
        return [new DataQuestion(DataQuestionKind.Rows), .. TableDesign.Questions(audit, draft)];
    }

    /// <summary>The DDL for the audit table and trigger of <paramref name="table"/>, and what speaks against it.</summary>
    /// <param name="baseColumns">The table's columns – in the designer the ones it will have after the draft ran.</param>
    /// <param name="facts">Counts about the existing audit table (<see cref="Questions"/>); <see cref="DataFacts.None"/> if there is none.</param>
    /// <param name="renamed">Columns of the table the designer renames (old name → new name).</param>
    /// <param name="taken">Names of the schema's objects: a new audit table cannot take one (ORA-00955).</param>
    /// <param name="rewriteTrigger">Write the trigger even if it looks up to date (the template changed).</param>
    public static DesignPlan Plan(AuditTemplate template, TableRef table, IReadOnlyList<ColumnInfo> baseColumns, AuditState state, DataFacts facts,
        IReadOnlyDictionary<string, string>? renamed = null, IEnumerable<string>? taken = null, bool rewriteTrigger = false)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(baseColumns);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(facts);
        var names = template.NamesFor(table);
        var shape = template.TableFor(table, baseColumns);
        var trigger = template.TriggerFor(table, baseColumns);
        List<string> problems = [.. names.Problems, .. shape.Problems, .. trigger.Problems];
        List<string> warnings = [.. shape.Warnings, .. trigger.Warnings];
        if (problems.Count > 0)
        {
            return new DesignPlan([], problems.Distinct().ToList(), warnings.Distinct().ToList());
        }

        var tablePlan = state.AuditTable is { } audit
            ? TableDesign.Plan(DesignContext.Of(audit), Draft(template, table, baseColumns, audit, facts, renamed, warnings, problems, [], []), facts)
            : TableDesign.PlanNew(new NewTable(shape.Table.Owner, shape.Table.Name, shape.Comment), new TableDraft(shape.Columns.Select(c => new ColumnDraft(null, c)).ToList()),
                new Dictionary<TableRef, TableDetails>(), taken ?? []);
        problems.AddRange(tablePlan.Problems);
        warnings.AddRange(tablePlan.Warnings);

        var steps = tablePlan.Steps.ToList();
        if (state.AuditTable is { } existing && shape.Comment is { Length: > 0 } comment && state.AuditTableComment != comment)
        {
            steps.Add(new DdlStep(existing.Table.Name, DdlStepKind.Comment, OracleDdl.CommentOnTable(existing.Table.Ref, comment)));
        }

        // New or renamed audit columns: the trigger must fill them, also when only the template's own columns changed.
        var columnsChanged = steps.Any(s => s.Kind is DdlStepKind.CreateTable or DdlStepKind.AddColumn or DdlStepKind.RenameColumn);
        if (TriggerStep(state.Trigger, table, trigger, rewriteTrigger || columnsChanged, warnings, problems) is { } step)
        {
            steps.Add(step);
        }

        return new DesignPlan(problems.Count == 0 ? DdlScript.Order(steps) : [], problems.Distinct().ToList(), warnings.Distinct().ToList());
    }

    /// <summary>
    /// The base table's plan of the designer together with its audit plan: one script, ordered by <see cref="DdlScript.Order"/>
    /// – the base table's new columns exist before the audit table gets them, the trigger comes last.
    /// </summary>
    public static DesignPlan Combine(DesignPlan table, DesignPlan audit)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(audit);
        return new DesignPlan(DdlScript.Order([.. table.Steps, .. audit.Steps]), [.. table.Problems, .. audit.Problems.Except(table.Problems)],
            [.. table.Warnings, .. audit.Warnings.Except(table.Warnings)]);
    }

    /// <summary>
    /// The table's columns after the designer's draft ran – kept, renamed and new ones in the draft's order, positions
    /// numbered again (the template's loops follow them) – and the renames (old name → new name).
    /// </summary>
    public static (IReadOnlyList<ColumnInfo> Columns, IReadOnlyDictionary<string, string> Renamed) After(TableDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var kept = draft.Columns.Where(c => !c.Dropped && c.Column.Name.Trim().Length > 0).ToList();
        var columns = kept.Select((c, i) => c.Column with { Name = c.Column.Name.Trim(), Position = i + 1 }).ToList();
        var renamed = kept.Where(c => c.Original is { } original && original != c.Column.Name.Trim())
            .ToDictionary(c => c.Original!, c => c.Column.Name.Trim(), StringComparer.Ordinal);
        return (columns, renamed);
    }

    /// <summary>
    /// The existing audit columns that do not take every value whole, and what bringing the audit table up to date changes
    /// without touching its values (types that grow, NOT NULL taken away, comments), in the audit table's order – the same
    /// draft as <see cref="Plan"/>, whose problems and warnings say the issues as sentences. None without an audit table.
    /// </summary>
    public static AuditReview Review(AuditTemplate template, TableRef table, IReadOnlyList<ColumnInfo> baseColumns, AuditState state)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(state);
        if (state.AuditTable is not { } audit)
        {
            return AuditReview.None;
        }

        var issues = new List<AuditColumnIssue>();
        var changes = new List<AuditChange>();
        Draft(template, table, baseColumns, audit, DataFacts.None, null, [], [], issues, changes);
        if (template.TableFor(table, baseColumns).Comment is { Length: > 0 } comment && state.AuditTableComment != comment)
        {
            changes.Add(new AuditChange(audit.Table.Name, AuditChangeKind.TableComment, state.AuditTableComment, comment)); // as Plan has it
        }

        return new AuditReview(issues, changes);
    }

    /// <summary>The audit table as it should be: its columns kept or widened, the template's and the table's new columns added.</summary>
    private static TableDraft Draft(AuditTemplate template, TableRef table, IReadOnlyList<ColumnInfo> baseColumns, TableDetails audit, DataFacts facts,
        IReadOnlyDictionary<string, string>? renamed, List<string> warnings, List<string> problems, List<AuditColumnIssue> issues, List<AuditChange> changes)
    {
        var shape = template.TableFor(table, baseColumns);
        var wanted = shape.Columns.ToDictionary(c => c.Name, StringComparer.Ordinal);
        var own = template.ColumnsBefore.Concat(template.ColumnsAfter).Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        var has = audit.Columns.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        var drafts = new List<ColumnDraft>();
        var matched = new HashSet<string>(StringComparer.Ordinal);
        foreach (var existing in audit.Columns)
        {
            // A column the designer renames in the table is renamed here too – unless the audit table has the new name
            // already (a swap, an old column of that name): then its history and the renamed column's would mix (R5 review).
            var name = existing.Name;
            if (renamed?.GetValueOrDefault(existing.Name) is { } to && wanted.ContainsKey(to))
            {
                if (has.Contains(to))
                {
                    warnings.Add(TextFormat.Format(AuditText.PlanRenameSkipped, existing.Name, to, audit.Table.Name));
                }
                else
                {
                    name = to;
                }
            }

            if (!wanted.TryGetValue(name, out var target))
            {
                if (!existing.Nullable && !existing.IsIdentity)
                {
                    warnings.Add(TextFormat.Format(AuditText.PlanColumnGoneNullable, existing.Name, audit.Table.Name));
                }

                var gone = existing.IsIdentity ? existing : existing with { Nullable = true };
                Changed(existing, gone, changes);
                drafts.Add(new ColumnDraft(existing.Name, gone));
                continue;
            }

            matched.Add(name);
            var kept = Kept(existing, target, own.Contains(name), audit.Table.Name, warnings, problems, issues);
            Changed(existing, kept, changes);
            drafts.Add(new ColumnDraft(existing.Name, kept with { Name = name }));
        }

        // New columns go to the end (ALTER TABLE … ADD appends); the trigger names its columns, the order does not matter.
        var rows = facts.Of(new DataQuestion(DataQuestionKind.Rows));
        foreach (var column in shape.Columns.Where(c => !matched.Contains(c.Name)))
        {
            var mandatory = !column.Nullable && !column.IsIdentity && string.IsNullOrWhiteSpace(column.Default);
            if (mandatory && rows is not 0)
            {
                warnings.Add(TextFormat.Format(AuditText.PlanNewColumnNullable, column.Name, audit.Table.Name));
            }

            drafts.Add(new ColumnDraft(null, mandatory && rows is not 0 ? column with { Nullable = true } : column));
        }

        return new TableDraft(drafts);
    }

    /// <summary>What the draft changes in an existing audit column, as the designer's writer turns it into steps.</summary>
    private static void Changed(ColumnInfo before, ColumnInfo after, List<AuditChange> changes)
    {
        if (OracleTypes.DdlType(before) != OracleTypes.DdlType(after))
        {
            changes.Add(new AuditChange(before.Name, AuditChangeKind.TypeGrows, OracleTypes.DisplayType(before), OracleTypes.DisplayType(after)));
        }

        if (!before.Nullable && after.Nullable)
        {
            changes.Add(new AuditChange(before.Name, AuditChangeKind.BecomesNullable, null, null));
        }

        if (!string.Equals(before.Comment, after.Comment, StringComparison.Ordinal))
        {
            changes.Add(new AuditChange(before.Name, AuditChangeKind.Comment, before.Comment, after.Comment));
        }
    }

    /// <summary>
    /// An existing audit column against what the template wants: the comment follows, NOT NULL is only ever taken away
    /// (older rows may lack the value), the type only grows – to the wanted type if that holds every value the audit
    /// column has (DATE → TIMESTAMP, longer, more digits). What the trigger writes is an INSERT, not a MODIFY
    /// (<see cref="OracleTypes.FitOf"/>, WP-51): an audit column that takes the values only with a loss (TIMESTAMP into
    /// DATE) stays with a warning, one that cannot take them (another type family) is a problem – the trigger would make
    /// every DML on the table fail (R5 review). Text shorter than the values can be grows to a length that holds both
    /// (<see cref="OracleTypes.Widened"/>, decision of the user 2026-10-26: <c>VARCHAR2(30)</c> for <c>VARCHAR2(10 CHAR)</c>
    /// → <c>VARCHAR2(30 CHAR)</c>); only where it cannot is it a problem, a longer value would fail. A BYTE column of four
    /// bytes per character takes a CHAR column's values whole and stays. A column of the template's own that
    /// is wider than the template's (TIMESTAMP WITH TIME ZONE against DATE) stays with a warning: the audit table probably
    /// comes from another template. Each case is an <see cref="AuditColumnIssue"/>; its message says whether the type is
    /// the template's or the table's. Identity and virtual columns stay as they are.
    /// </summary>
    /// <param name="ofTemplate">The column is one of the template's own, not a copy of a column of the table.</param>
    private static ColumnInfo Kept(ColumnInfo existing, ColumnInfo target, bool ofTemplate, string auditTable, List<string> warnings, List<string> problems,
        List<AuditColumnIssue> issues)
    {
        var kept = existing with { Comment = string.IsNullOrEmpty(target.Comment) ? existing.Comment : target.Comment };
        if (existing.IsIdentity || existing.IsVirtual)
        {
            return kept;
        }

        if (!existing.Nullable && target.Nullable)
        {
            kept = kept with { Nullable = true };
        }

        var change = OracleTypes.ChangeOf(existing, target);
        if (change == ColumnTypeChange.Same)
        {
            return kept;
        }

        var fit = OracleTypes.FitOf(existing, target);
        if (fit == ValueFit.Full)
        {
            if (ofTemplate)
            {
                Report(AuditColumnFit.WiderThanTemplate);
            }

            return kept;
        }

        if (change == ColumnTypeChange.Free && OracleTypes.FitOf(target, existing) == ValueFit.Full)
        {
            return kept with
            {
                DataType = target.DataType, Length = target.Length, CharSemantics = target.CharSemantics, Precision = target.Precision, Scale = target.Scale,
            };
        }

        if (OracleTypes.Widened(existing, target) is { } widened)
        {
            return kept with { Length = widened.Length, CharSemantics = widened.CharSemantics };
        }

        Report(fit switch
        {
            ValueFit.Lossy => AuditColumnFit.Lossy,
            ValueFit.Partial => AuditColumnFit.TooShort,
            _ => AuditColumnFit.Incompatible,
        });
        return kept;

        void Report(AuditColumnFit how)
        {
            var issue = new AuditColumnIssue(existing.Name, how, ofTemplate, OracleTypes.DisplayType(existing), OracleTypes.DisplayType(target));
            issues.Add(issue);
            (issue.IsProblem ? problems : warnings).Add(issue.Message(auditTable));
        }
    }

    /// <summary>
    /// The trigger, if it is missing, invalid or outdated: it references other columns than the template's (columns of the
    /// table added, dropped or renamed – an invalid trigger's columns are unknown, ADR 0021) or the audit table got new
    /// columns. A trigger of that name on another table is a problem, not replaced.
    /// </summary>
    private static DdlStep? TriggerStep(AuditTriggerState? state, TableRef table, AuditTrigger trigger, bool rewrite, List<string> warnings, List<string> problems)
    {
        if (state is { Table: var on } && on != table)
        {
            problems.Add(TextFormat.Format(AuditText.PlanTriggerOnOtherTable, trigger.Name, on is null ? "–" : $"{on.Owner}.{on.Name}"));
            return null;
        }

        var outdated = state is null || !state.Valid || rewrite
                       || !state.Columns.ToHashSet(StringComparer.Ordinal).SetEquals(trigger.Columns);
        if (!outdated)
        {
            if (!state!.Enabled)
            {
                warnings.Add(TextFormat.Format(AuditText.PlanTriggerDisabled, trigger.Name));
            }

            return null;
        }

        if (state is { Enabled: false })
        {
            warnings.Add(TextFormat.Format(AuditText.PlanTriggerEnabled, trigger.Name));
        }

        return new DdlStep(table.Name, DdlStepKind.PlSqlObject, trigger.Sql);
    }
}
