using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Compare;

/// <summary>What a row of the comparison matrix is.</summary>
public enum CompareKind
{
    Table,
    View,
    MaterializedView,
    Column,
    PrimaryKey,
    Unique,
    ForeignKey,
    Check,
    Index,
}

/// <summary>How a side looks for one row.</summary>
public enum CellState
{
    /// <summary>Like the reference; without a reference: all sides that have the object agree.</summary>
    Same,

    /// <summary>Differs from the reference; without a reference: the sides that have the object do not all agree (see <see cref="CompareCell.Group"/>).</summary>
    Different,

    /// <summary>The side does not have the object.</summary>
    Missing,

    /// <summary>
    /// The side has the object only under a name in another letter case (<c>Kunden</c> against <c>KUNDEN</c>). For Oracle
    /// and EF Core (which quotes names) these are two names; shown as its own difference.
    /// </summary>
    OtherCase,
}

/// <summary>Options of a comparison (decisions of the user, docs/work-packages.md, WP-20).</summary>
/// <param name="Reference">Index of the side the others are measured against; null = no reference, sides grouped by equal definition.</param>
/// <param name="ColumnOrder">Whether the order of columns counts (default: no).</param>
public sealed record CompareOptions(int? Reference = null, bool ColumnOrder = false);

/// <summary>One side of a row.</summary>
/// <param name="Group">
/// Sides with the same group have the same definition (0, 1 … in order of first appearance from the left); -1 if the
/// side does not have the object. Lets the view colour variants without a reference.
/// </param>
/// <param name="Definition">The compared definition as one line of text, e.g. <c>VARCHAR2(200 CHAR) NOT NULL DEFAULT 'x'</c>; null if missing.</param>
public sealed record CompareCell(CellState State, int Group, string? Definition)
{
    /// <summary>The side's name of the object when it differs from the row's name (<see cref="CellState.OtherCase"/>, generated constraint names).</summary>
    public string? Name { get; init; }

    /// <summary>The object itself on this side, for DDL generation and details: set for table/view rows.</summary>
    public ObjectSnapshot? Object { get; init; }

    /// <summary>Set for column rows.</summary>
    public ColumnInfo? Column { get; init; }

    /// <summary>Set for primary key, unique, foreign key and check rows.</summary>
    public ConstraintInfo? Constraint { get; init; }

    /// <summary>Set for index rows.</summary>
    public IndexInfo? Index { get; init; }
}

/// <summary>
/// A row of the matrix: an object (table, view, materialized view) with its columns, constraints and indexes as
/// children, or one of those children.
/// </summary>
/// <param name="Key">Stable within a comparison, e.g. <c>KUNDEN</c>, <c>KUNDEN/col/EMAIL</c>, <c>KUNDEN/idx/…</c>.</param>
/// <param name="Name">
/// Shown name: object or column name; a constraint or index name, or – for names Oracle generated (<c>SYS_C…</c>,
/// matched by content) – a description such as <c>CHECK (MENGE &gt; 0)</c> or <c>INDEX (KUNDE_ID)</c>.
/// </param>
/// <param name="Cells">One per side, in the order of <see cref="SchemaComparison.Sides"/>.</param>
/// <param name="Children">Columns, then constraints, then indexes (object rows only).</param>
public sealed record CompareRow(string Key, CompareKind Kind, string Name, IReadOnlyList<CompareCell> Cells, IReadOnlyList<CompareRow> Children)
{
    /// <summary>The row itself differs between sides (missing, different, other case).</summary>
    public bool Differs => Cells.Any(c => c.State != CellState.Same);

    /// <summary>The row or one of its children differs.</summary>
    public bool HasDifferences => Differs || Children.Any(c => c.HasDifferences);
}

/// <summary>Result of <see cref="SchemaDiff.Compare"/>: the matrix, objects ordered by name.</summary>
public sealed record SchemaComparison(IReadOnlyList<SchemaSnapshot> Sides, CompareOptions Options, IReadOnlyList<CompareRow> Objects);
