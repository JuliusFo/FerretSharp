using System.Text;
using FerretSharp.Core.Oracle;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Audit;

/// <summary>How the audit table takes over the base table's columns (<c>-- @base-columns as-base|nullable</c>).</summary>
public enum AuditBaseColumns
{
    /// <summary>NOT NULL as in the base table.</summary>
    AsBase,

    /// <summary>Always nullable.</summary>
    Nullable,
}

/// <summary>The names an audit template gives a base table's audit table and trigger, both in the base table's schema.</summary>
/// <param name="Trigger">Name of the trigger (exact dictionary name); its owner is the owner of <see cref="AuditTable"/>.</param>
public sealed record AuditNames(TableRef AuditTable, string Trigger)
{
    /// <summary>Why a name cannot be used (empty, too long, double quotes, the base table itself); empty if both are fine.</summary>
    public IReadOnlyList<string> Problems { get; init; } = [];
}

/// <summary>The audit table as the template wants it.</summary>
/// <param name="Columns">The template's columns before the base columns, the base columns, the template's after them;
/// positions 1..n, comments set (the template's, the base table's).</param>
/// <param name="Comment">Table comment from <c>@table-comment</c>; null without it.</param>
/// <param name="Warnings">Base columns left out (LONG).</param>
public sealed record AuditTableShape(TableRef Table, IReadOnlyList<ColumnInfo> Columns, string? Comment, IReadOnlyList<string> Warnings)
{
    /// <summary>Why the table cannot be created like this (names, a column in both the template and the base table).</summary>
    public IReadOnlyList<string> Problems { get; init; } = [];
}

/// <param name="Name">Exact dictionary name of the trigger, in the schema <paramref name="Owner"/>.</param>
/// <param name="Sql"><c>CREATE OR REPLACE TRIGGER …</c> as the template writes it, ending with its <c>END;</c> (no <c>/</c> line).</param>
/// <param name="Columns">The base columns the trigger's loops name, in table order – what <c>ALL_TRIGGER_COLS</c> lists for an up-to-date trigger.</param>
/// <param name="Warnings">Base columns left out (LONG, not comparable), loops without columns.</param>
public sealed record AuditTrigger(string Owner, string Name, string Sql, IReadOnlyList<string> Columns, IReadOnlyList<string> Warnings)
{
    /// <summary>Why the trigger cannot be created like this (names).</summary>
    public IReadOnlyList<string> Problems { get; init; } = [];
}

/// <param name="Line">Line in the template (1-based); 0 for the template as a whole (a directive is missing).</param>
public sealed record AuditTemplateError(int Line, string Message)
{
    public override string ToString() => Line > 0 ? TextFormat.Format(AuditText.ErrorInLine, Line, Message) : Message;
}

/// <summary>The template has errors; <see cref="Errors"/> lists them with their lines, the message too.</summary>
public sealed class AuditTemplateException(IReadOnlyList<AuditTemplateError> errors)
    : Exception(AuditText.TemplateHasErrors + "\n" + string.Join("\n", errors))
{
    public IReadOnlyList<AuditTemplateError> Errors { get; } = errors;
}

/// <summary>
/// A parsed audit template (WP-26, <c>docs/audit-templates.md</c>): turns a base table and its columns into the audit table
/// as it should be and the trigger that fills it. Pure text work – what exists in the database and what has to change is
/// the caller's business.
/// </summary>
public sealed record AuditTemplate
{
    /// <summary>Oracle's maximum name length in bytes (12.2 and later).</summary>
    public const int MaxNameBytes = 128;

    internal AuditTemplate(
        string text,
        IReadOnlyList<PatternPart> auditTable,
        IReadOnlyList<PatternPart> trigger,
        IReadOnlyList<PatternPart>? tableComment,
        IReadOnlyList<ColumnInfo> columnsBefore,
        AuditBaseColumns baseColumns,
        IReadOnlyList<ColumnInfo> columnsAfter,
        IReadOnlyList<PatternPart> triggerSource)
    {
        Text = text;
        AuditTablePattern = auditTable;
        TriggerPattern = trigger;
        TableCommentPattern = tableComment;
        ColumnsBefore = columnsBefore;
        BaseColumns = baseColumns;
        ColumnsAfter = columnsAfter;
        TriggerSource = triggerSource;
    }

    /// <summary>The template as read.</summary>
    public string Text { get; }

    /// <summary>The template's own columns before the base columns (positions 0, no owner of their own).</summary>
    public IReadOnlyList<ColumnInfo> ColumnsBefore { get; }

    /// <summary>How base columns are taken over.</summary>
    public AuditBaseColumns BaseColumns { get; }

    /// <summary>The template's own columns after the base columns.</summary>
    public IReadOnlyList<ColumnInfo> ColumnsAfter { get; }

    internal IReadOnlyList<PatternPart> AuditTablePattern { get; }

    internal IReadOnlyList<PatternPart> TriggerPattern { get; }

    internal IReadOnlyList<PatternPart>? TableCommentPattern { get; }

    internal IReadOnlyList<PatternPart> TriggerSource { get; }

    /// <summary>Audit table and trigger name for <paramref name="table"/>, in its schema.</summary>
    public AuditNames NamesFor(TableRef table)
    {
        var auditTable = AuditPattern.Render(AuditTablePattern, (p, _) => Plain(p, table, null, null), _ => []);
        var trigger = AuditPattern.Render(TriggerPattern, (p, _) => Plain(p, table, auditTable, null), _ => []);
        var problems = new List<string>();
        CheckName(auditTable, "audit-table", problems);
        CheckName(trigger, "trigger", problems);
        if (auditTable == table.Name)
        {
            problems.Add(TextFormat.Format(AuditText.AuditTableIsBaseTable, table.Name));
        }

        return new AuditNames(new TableRef(table.Owner, auditTable), trigger) { Problems = problems };
    }

    /// <summary>
    /// The audit table for <paramref name="table"/> with <paramref name="baseColumns"/> (dictionary order): the template's
    /// columns around copies of the base columns. A copy keeps name, type and comment; NOT NULL only with
    /// <see cref="AuditBaseColumns.AsBase"/>; never a default or identity (an audit row is a copy, not a new row); a virtual
    /// column becomes an ordinary column of its type (the trigger stores the value it had). LONG columns are left out.
    /// </summary>
    public AuditTableShape TableFor(TableRef table, IReadOnlyList<ColumnInfo> baseColumns)
    {
        var names = NamesFor(table);
        var warnings = LongWarnings(baseColumns);
        var copies = Usable(baseColumns).Select(c => new ColumnInfo(
            c.Name, c.DataType, c.Length, c.CharSemantics, c.Precision, c.Scale,
            Nullable: BaseColumns == AuditBaseColumns.Nullable || c.Nullable,
            IsIdentity: false, Default: null, Position: 0, Comment: c.Comment)).ToList();

        var problems = names.Problems.ToList();
        var own = ColumnsBefore.Concat(ColumnsAfter).Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        problems.AddRange(copies.Where(c => own.Contains(c.Name)).Select(c => TextFormat.Format(AuditText.ColumnClash, c.Name, table.Name)));

        var columns = ColumnsBefore.Concat(copies).Concat(ColumnsAfter).Select((c, i) => c with { Position = i + 1 }).ToList();
        var comment = TableCommentPattern is null
            ? null
            : AuditPattern.Render(TableCommentPattern, (p, _) => Plain(p, table, names.AuditTable.Name, names.Trigger), _ => []);
        return new AuditTableShape(names.AuditTable, columns, comment, warnings) { Problems = problems };
    }

    /// <summary>
    /// The trigger for <paramref name="table"/> with <paramref name="baseColumns"/>. Names in the source are written as
    /// identifiers: plain where Oracle reads them as written, quoted exactly otherwise; <c>{table}</c>,
    /// <c>{audit_table}</c> and <c>{trigger}</c> with their owner, so the trigger lands in the base table's schema whatever
    /// the session's current schema.
    /// </summary>
    public AuditTrigger TriggerFor(TableRef table, IReadOnlyList<ColumnInfo> baseColumns)
    {
        var names = NamesFor(table);
        var usable = Usable(baseColumns);
        var warnings = LongWarnings(baseColumns);
        IReadOnlyList<ColumnInfo> ColumnsOf(LoopPart loop) => loop.Comparable ? usable.Where(c => OracleTypes.IsComparable(c.DataType)).ToList() : usable;

        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var loop in AuditPattern.Loops(TriggerSource))
        {
            var columns = ColumnsOf(loop);
            used.UnionWith(columns.Select(c => c.Name));
            if (columns.Count == 0)
            {
                warnings.Add(TextFormat.Format(AuditText.LoopEmpty, loop.Line));
            }

            if (loop.Comparable && usable.Where(c => !OracleTypes.IsComparable(c.DataType)).Select(c => c.Name).ToList() is { Count: > 0 } skipped)
            {
                warnings.Add(TextFormat.Format(AuditText.NotComparable, loop.Line, string.Join(", ", skipped)));
            }
        }

        string Value(AuditPlaceholder placeholder, ColumnInfo? column) => placeholder switch
        {
            AuditPlaceholder.Table => Qualified(table.Owner, table.Name),
            AuditPlaceholder.AuditTable => Qualified(table.Owner, names.AuditTable.Name),
            AuditPlaceholder.Trigger => Qualified(table.Owner, names.Trigger),
            AuditPlaceholder.Owner => Identifier(table.Owner),
            AuditPlaceholder.Column => Identifier(column!.Name),
            AuditPlaceholder.Type => OracleTypes.DdlType(column!),
            _ => throw new ArgumentOutOfRangeException(nameof(placeholder)),
        };

        var sql = AuditPattern.Render(TriggerSource, Value, ColumnsOf);
        return new AuditTrigger(
            table.Owner, names.Trigger, sql, usable.Where(c => used.Contains(c.Name)).Select(c => c.Name).ToList(), warnings.Distinct().ToList())
        {
            Problems = names.Problems,
        };
    }

    /// <summary>The base columns a trigger can see, in table order: all but LONG and LONG RAW.</summary>
    private static List<ColumnInfo> Usable(IReadOnlyList<ColumnInfo> baseColumns) =>
        baseColumns.OrderBy(c => c.Position).Where(c => !OracleTypes.IsLong(c.DataType)).ToList();

    private static List<string> LongWarnings(IReadOnlyList<ColumnInfo> baseColumns) =>
        baseColumns.Where(c => OracleTypes.IsLong(c.DataType)).Select(c => TextFormat.Format(AuditText.LongLeftOut, c.Name, c.DataType)).ToList();

    /// <summary>Placeholders of the directives: the plain dictionary names.</summary>
    private static string Plain(AuditPlaceholder placeholder, TableRef table, string? auditTable, string? trigger) => placeholder switch
    {
        AuditPlaceholder.Table => table.Name,
        AuditPlaceholder.Owner => table.Owner,
        AuditPlaceholder.AuditTable => auditTable ?? throw new InvalidOperationException("{audit_table} is not available here."),
        AuditPlaceholder.Trigger => trigger ?? throw new InvalidOperationException("{trigger} is not available here."),
        _ => throw new InvalidOperationException($"{placeholder} is not available in a directive."),
    };

    private static string Identifier(string name) =>
        name.Length == 0 || name.Contains('"', StringComparison.Ordinal) ? name : OracleIdentifier.QuoteIfNeeded(name);

    private static string Qualified(string owner, string name) => $"{Identifier(owner)}.{Identifier(name)}";

    private static void CheckName(string name, string directive, List<string> problems)
    {
        if (name.Trim().Length == 0)
        {
            problems.Add(TextFormat.Format(AuditText.NameEmpty, directive));
        }
        else if (name.Contains('"', StringComparison.Ordinal) || name.Contains('\0', StringComparison.Ordinal))
        {
            problems.Add(TextFormat.Format(AuditText.NameInvalid, name));
        }
        else if (Encoding.UTF8.GetByteCount(name) > MaxNameBytes)
        {
            problems.Add(TextFormat.Format(AuditText.NameTooLong, name, MaxNameBytes));
        }
    }
}
