using System.Text.RegularExpressions;

namespace FerretSharp.Core.Schema;

/// <summary>Overview of an object (header above the detail views). Loaded on demand.</summary>
/// <param name="Status"><c>ALL_OBJECTS.STATUS</c>: VALID, INVALID or N/A.</param>
/// <param name="NumRows">Row count of the last statistics gathering (<c>ALL_TABLES.NUM_ROWS</c>); null for views or without statistics.</param>
public sealed record ObjectInfo(
    string Status,
    DateTime? Created,
    DateTime? LastDdl,
    string? Comment,
    long? NumRows,
    DateTime? LastAnalyzed,
    string? Tablespace,
    bool Partitioned,
    bool Temporary)
{
    public bool IsInvalid => Status == "INVALID";
}

public enum ConstraintType
{
    PrimaryKey,
    Unique,
    ForeignKey,
    Check,

    /// <summary><c>WITH CHECK OPTION</c> of a view.</summary>
    ViewCheckOption,

    /// <summary><c>WITH READ ONLY</c> of a view.</summary>
    ViewReadOnly,
    Other,
}

/// <summary>How a kind of constraint is named – one rule for the designer, the constraints view and the plan (review 3.54.0, B5).</summary>
public static class ConstraintTypes
{
    /// <summary>As in DDL (<c>PRIMARY KEY</c>, <c>CHECK OPTION</c> …); null for <see cref="ConstraintType.Other"/>.</summary>
    public static string? Keyword(ConstraintType type) => type switch
    {
        ConstraintType.PrimaryKey => "PRIMARY KEY",
        ConstraintType.Unique => "UNIQUE",
        ConstraintType.ForeignKey => "FOREIGN KEY",
        ConstraintType.Check => "CHECK",
        ConstraintType.ViewCheckOption => "CHECK OPTION",
        ConstraintType.ViewReadOnly => "READ ONLY",
        _ => null,
    };

    /// <summary>The badge of a key (<c>PK</c>, <c>UK</c>, <c>FK</c>); <c>CHECK</c> for the rest.</summary>
    public static string Short(ConstraintType type) => type switch
    {
        ConstraintType.PrimaryKey => "PK",
        ConstraintType.Unique => "UK",
        ConstraintType.ForeignKey => "FK",
        _ => "CHECK",
    };
}

/// <param name="Columns">Constrained columns in key order; empty for most check constraints on several columns.</param>
/// <param name="Condition">Check condition (<c>SEARCH_CONDITION</c>); null for other types.</param>
/// <param name="References">Referenced table of a foreign key.</param>
/// <param name="DeleteRule">CASCADE, SET NULL or NO ACTION for foreign keys.</param>
/// <param name="GeneratedName">Oracle named it (<c>SYS_C…</c>).</param>
public sealed record ConstraintInfo(
    string Name,
    ConstraintType Type,
    IReadOnlyList<string> Columns,
    string? Condition,
    TableRef? References,
    IReadOnlyList<string> ReferencedColumns,
    string? DeleteRule,
    bool Enabled,
    bool Validated,
    bool Deferrable,
    bool InitiallyDeferred,
    bool GeneratedName)
{
    private static readonly Regex NotNullCondition = new("""\A\s*"[^"]+"\s+IS\s+NOT\s+NULL\s*\z""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The check Oracle creates for a <c>NOT NULL</c> column; shown with the columns, not as a constraint.</summary>
    public bool IsColumnNotNull => Type == ConstraintType.Check && GeneratedName && Condition is { } c && NotNullCondition.IsMatch(c);

    /// <summary>A foreign key's <c>ON DELETE</c> rule (<c>CASCADE</c>, <c>SET NULL</c>); null for none.</summary>
    public string? OnDelete => RuleOf(DeleteRule);

    /// <summary>A delete rule as the dictionary or a draft has it: <c>NO ACTION</c> and an empty one are none (null).</summary>
    public static string? RuleOf(string? rule) => string.IsNullOrEmpty(rule) || rule == "NO ACTION" ? null : rule;
}

/// <param name="Name">Column name, or the expression of a function-based index.</param>
/// <param name="IsExpression">True for a function-based column (not a plain column).</param>
public sealed record IndexColumn(string Name, bool IsExpression, bool Descending);

/// <param name="IndexType">e.g. NORMAL, BITMAP, FUNCTION-BASED NORMAL, IOT - TOP.</param>
/// <param name="Status">VALID or UNUSABLE; N/A for partitioned indexes (status per partition).</param>
public sealed record IndexInfo(
    string Owner,
    string Name,
    string IndexType,
    bool Unique,
    string Status,
    IReadOnlyList<IndexColumn> Columns,
    string? Tablespace,
    bool Partitioned,
    bool Visible)
{
    public bool IsUnusable => Status == "UNUSABLE";

    /// <summary>
    /// Oracle named it: <c>SYS_C…</c> for the index of a key, <c>SYS_AI_…</c> from automatic indexing and the like. Such
    /// indexes are matched by content and their names are never copied (schema comparison and DDL proposal alike).
    /// </summary>
    public bool GeneratedName => IsGenerated(Name);

    public static bool IsGenerated(string name) => name.StartsWith("SYS_", StringComparison.Ordinal);
}

/// <param name="Type"><c>ALL_OBJECTS.OBJECT_TYPE</c>, e.g. TABLE, VIEW, PACKAGE BODY, SYNONYM.</param>
/// <param name="Status">VALID or INVALID; null if the object is not visible to the session.</param>
public sealed record DependencyInfo(string Owner, string Name, string Type, string? Status)
{
    public bool IsInvalid => Status == "INVALID";
}

/// <param name="Uses">Objects this one depends on (e.g. the tables of a view).</param>
/// <param name="UsedBy">Objects that depend on this one (views, packages, triggers, synonyms …).</param>
public sealed record ObjectDependencies(IReadOnlyList<DependencyInfo> Uses, IReadOnlyList<DependencyInfo> UsedBy);

public static class IndexAdvice
{
    /// <summary>
    /// Foreign keys without an index starting with their columns (in any order). Oracle then locks the whole child
    /// table when a parent row is deleted or its key updated, and every such check scans the child table.
    /// </summary>
    public static IReadOnlyList<ForeignKeyInfo> UnindexedForeignKeys(IEnumerable<ForeignKeyInfo> foreignKeys, IReadOnlyList<IndexInfo> indexes) =>
        foreignKeys.Where(fk => !indexes.Any(index => Covers(index, fk.FromColumns))).ToList();

    /// <summary>
    /// An index Oracle can use to find rows by the column: one that starts with it, visible and usable. Code that filters by
    /// a column without one (WP-40) may read the whole table; whether it does, only the plan tells.
    /// </summary>
    public static bool HasIndexFor(string column, IReadOnlyList<IndexInfo> indexes) =>
        indexes.Any(index => index.Visible && !index.IsUnusable && Covers(index, [column]));

    /// <summary>The index starts with <paramref name="columns"/> (in any order), so lookups by them can use it.</summary>
    public static bool Covers(IndexInfo index, IReadOnlyList<string> columns)
    {
        if (index.Columns.Count < columns.Count)
        {
            return false;
        }

        var leading = index.Columns.Take(columns.Count).ToList();
        return leading.All(c => !c.IsExpression) && leading.Select(c => c.Name).ToHashSet(StringComparer.Ordinal).SetEquals(columns);
    }
}

/// <summary>Names a table's new or renamed constraints and indexes cannot take (<see cref="ISchemaReader.GetNamesElsewhereAsync"/>).</summary>
/// <param name="Constraints">Constraints of the owner's other tables (named by a user).</param>
/// <param name="Indexes">
/// The owner's indexes of other tables: indexes have a namespace of their own (a table and an index may share a name), and
/// so does the index a new primary or unique key creates (review of R8).
/// </param>
public sealed record SchemaNames(IReadOnlySet<string> Constraints, IReadOnlySet<string> Indexes)
{
    public static readonly SchemaNames None = new(new HashSet<string>(), new HashSet<string>());
}