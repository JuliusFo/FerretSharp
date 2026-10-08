using FerretSharp.Core.Compare;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Tests.Compare;

/// <summary>
/// Builds comparisons for the DDL tests the way <see cref="SchemaDiff"/> describes them: rows per object with columns,
/// constraints and indexes as children, names exact (other case as <see cref="CellState.OtherCase"/>), generated
/// constraint (<c>SYS_C…</c>) and index names (<c>SYS_…</c>) matched by content. Deliberately small – not the real <see cref="SchemaDiff"/>.
/// Also linked into the integration tests.
/// </summary>
public static class TestComparison
{
    public static SchemaComparison Of(params SchemaSnapshot[] sides) => Of(new CompareOptions(Reference: 0), sides);

    public static SchemaComparison Of(CompareOptions options, params SchemaSnapshot[] sides)
    {
        var reference = options.Reference ?? 0;
        var objects = Rows(
            sides.Select(s => s.Objects.Select(o => (Key: o.Name, Name: o.Name, Item: o))).ToList(),
            o => $"{o.Kind}{(o.IsIndexOrganized ? " IOT" : "")}{(o.Temporary ? " TEMPORARY" : "")}{(o.Partitioned ? " PARTITIONED" : "")}",
            (cell, o) => cell with { Object = o },
            reference,
            (key, items) =>
            {
                var columns = Rows(
                    items.Select(o => (o?.Columns ?? []).Select(c => (Key: c.Name, Name: c.Name, Item: c))).ToList(),
                    c => ColumnDefinition(c, options.ColumnOrder),
                    (cell, c) => cell with { Column = c },
                    reference,
                    (_, _) => [],
                    $"{key}/col/");
                var constraints = Rows(
                    items.Select(o => (o?.Constraints ?? []).Where(c => !c.IsColumnNotNull).Select(c =>
                        (Key: c.GeneratedName ? ConstraintDefinition(c) : c.Name, Name: c.GeneratedName ? ConstraintDefinition(c) : c.Name, Item: c))).ToList(),
                    ConstraintDefinition,
                    (cell, c) => cell with { Constraint = c },
                    reference,
                    (_, _) => [],
                    $"{key}/con/",
                    KindOf);
                var indexes = Rows(
                    items.Select(o => (o?.Indexes ?? []).Select(i =>
                        (Key: i.GeneratedName ? IndexDefinition(i) : i.Name, Name: i.GeneratedName ? IndexDefinition(i) : i.Name, Item: i))).ToList(),
                    IndexDefinition,
                    (cell, i) => cell with { Index = i },
                    reference,
                    (_, _) => [],
                    $"{key}/idx/",
                    _ => CompareKind.Index);
                return [.. columns, .. constraints, .. indexes];
            },
            "",
            o => o.Kind switch { TableKind.View => CompareKind.View, TableKind.MaterializedView => CompareKind.MaterializedView, _ => CompareKind.Table });

        return new SchemaComparison(sides, options, objects.OrderBy(o => o.Name, StringComparer.Ordinal).ToList());
    }

    /// <summary>The definition text the rows carry; the DDL writer only compares it between two sides.</summary>
    public static string ColumnDefinition(ColumnInfo column, bool withPosition = false) =>
        $"{column.DisplayType}{(column.CharSemantics ? " C" : "")}{(column.Nullable ? "" : " NOT NULL")}"
        + (column.IsIdentity ? " IDENTITY" : column.IsVirtual ? $" AS ({column.Default})" : column.Default is { } d ? $" DEFAULT {(column.DefaultOnNull ? "ON NULL " : "")}{d}" : "")
        + (column.Comment is { } comment ? $" -- {comment}" : "")
        + (withPosition ? $" #{column.Position.ToString(System.Globalization.CultureInfo.InvariantCulture)}" : "");

    public static string ConstraintDefinition(ConstraintInfo c) => c.Type switch
    {
        ConstraintType.PrimaryKey => $"PRIMARY KEY ({string.Join(", ", c.Columns)})",
        ConstraintType.Unique => $"UNIQUE ({string.Join(", ", c.Columns)})",
        ConstraintType.ForeignKey => $"FOREIGN KEY ({string.Join(", ", c.Columns)}) REFERENCES {c.References?.Name} ({string.Join(", ", c.ReferencedColumns)}) {c.DeleteRule}",
        _ => $"CHECK ({c.Condition})",
    } + (c.Enabled ? "" : " DISABLED") + (c.Validated ? "" : " NOVALIDATE") + (c.Deferrable ? " DEFERRABLE" : "") + (c.InitiallyDeferred ? " DEFERRED" : "");

    public static string IndexDefinition(IndexInfo i) =>
        $"{(i.Unique ? "UNIQUE " : "")}{i.IndexType} INDEX ({string.Join(", ", i.Columns.Select(c => c.Name + (c.Descending ? " DESC" : "")))}){(i.Visible ? "" : " INVISIBLE")}";

    // ---- snapshot builders ----

    public static SchemaSnapshot Snapshot(string owner, params ObjectSnapshot[] objects) =>
        new(owner, DateTimeOffset.UnixEpoch, objects.OrderBy(o => o.Name, StringComparer.Ordinal).ToList());

    public static ObjectSnapshot Table(
        string name,
        IEnumerable<ColumnInfo> columns,
        IEnumerable<ConstraintInfo>? constraints = null,
        IEnumerable<IndexInfo>? indexes = null,
        TableKind kind = TableKind.Table,
        bool iot = false,
        bool temporary = false,
        bool partitioned = false) =>
        new(name, kind, columns.Select((c, i) => c with { Position = i + 1 }).ToList(), constraints?.ToList() ?? [], indexes?.ToList() ?? [], iot, temporary, partitioned);

    public static ColumnInfo Column(
        string name,
        string type,
        int? length = null,
        bool charSemantics = false,
        int? precision = null,
        int? scale = null,
        bool nullable = true,
        string? @default = null,
        bool identity = false,
        bool isVirtual = false,
        bool defaultOnNull = false,
        string? comment = null) =>
        new(name, type, length, charSemantics, precision, scale, nullable && !identity && !defaultOnNull, identity, @default, 0, comment, isVirtual, defaultOnNull);

    public static ConstraintInfo PrimaryKey(string name, params string[] columns) =>
        new(name, ConstraintType.PrimaryKey, columns, null, null, [], null, true, true, false, false, Generated(name));

    public static ConstraintInfo Unique(string name, params string[] columns) =>
        new(name, ConstraintType.Unique, columns, null, null, [], null, true, true, false, false, Generated(name));

    public static ConstraintInfo Check(string name, string condition, bool enabled = true) =>
        new(name, ConstraintType.Check, [], condition, null, [], null, enabled, enabled, false, false, Generated(name));

    public static ConstraintInfo ForeignKey(string name, string[] columns, TableRef references, string[] referencedColumns, string deleteRule = "NO ACTION", bool deferred = false) =>
        new(name, ConstraintType.ForeignKey, columns, null, references, referencedColumns, deleteRule, true, true, deferred, deferred, Generated(name));

    public static IndexInfo Index(string owner, string name, params IndexColumn[] columns) =>
        new(owner, name, columns.Any(c => c.IsExpression) ? "FUNCTION-BASED NORMAL" : "NORMAL", false, "VALID", columns, "USERS", false, true);

    public static IndexInfo UniqueIndex(string owner, string name, params IndexColumn[] columns) => Index(owner, name, columns) with { Unique = true };

    public static IndexColumn On(string column, bool descending = false) => new(column, false, descending);

    public static IndexColumn Expression(string expression, bool descending = false) => new(expression, true, descending);

    private static bool Generated(string name) => name.StartsWith("SYS_C", StringComparison.Ordinal);

    private static CompareKind KindOf(ConstraintInfo c) => c.Type switch
    {
        ConstraintType.PrimaryKey => CompareKind.PrimaryKey,
        ConstraintType.Unique => CompareKind.Unique,
        ConstraintType.ForeignKey => CompareKind.ForeignKey,
        _ => CompareKind.Check,
    };

    /// <summary>One row per key (case-folded), cells per side; rows ordered by name.</summary>
    private static List<CompareRow> Rows<T>(
        IReadOnlyList<IEnumerable<(string Key, string Name, T Item)>> sides,
        Func<T, string> definition,
        Func<CompareCell, T, CompareCell> payload,
        int reference,
        Func<string, IReadOnlyList<T?>, IReadOnlyList<CompareRow>> children,
        string keyPrefix,
        Func<T, CompareKind>? kind = null)
        where T : class
    {
        var perSide = sides.Select(s => s.ToList()).ToList();
        var groups = perSide.SelectMany(s => s.Select(i => i.Key)).Distinct(StringComparer.Ordinal)
            .GroupBy(k => k.ToUpperInvariant(), StringComparer.Ordinal);
        var rows = new List<CompareRow>();
        foreach (var group in groups)
        {
            // The row's name: as on the first side (left to right) that has the object.
            var (rowKey, rowName, _) = perSide.SelectMany(s => s.Where(i => group.Contains(i.Key, StringComparer.Ordinal)).Take(1)).First();
            var items = perSide.Select(s => Find(s, rowKey, group)).ToList();
            var definitions = items.Select(i => i.Item is null ? null : definition(i.Item)).ToList();
            var distinct = definitions.Where(d => d is not null).Distinct(StringComparer.Ordinal).ToList();
            var cells = items.Select((item, side) =>
            {
                if (item.Item is null)
                {
                    return new CompareCell(CellState.Missing, -1, null);
                }

                var state = item.OtherCase ? CellState.OtherCase
                    : string.Equals(definitions[side], definitions[reference], StringComparison.Ordinal) ? CellState.Same : CellState.Different;
                var cell = new CompareCell(state, distinct.IndexOf(definitions[side]!), definitions[side])
                {
                    Name = string.Equals(item.Name, rowName, StringComparison.Ordinal) ? null : item.Name,
                };
                return payload(cell, item.Item);
            }).ToList();
            var first = items.First(i => i.Item is not null).Item!;
            rows.Add(new CompareRow(keyPrefix + rowKey, kind?.Invoke(first) ?? CompareKind.Column, rowName, cells, children(keyPrefix + rowKey, items.Select(i => i.Item).ToList())));
        }

        return rows;
    }

    private static (T? Item, string? Name, bool OtherCase) Find<T>(List<(string Key, string Name, T Item)> side, string key, IEnumerable<string> group)
        where T : class
    {
        foreach (var item in side.Where(i => string.Equals(i.Key, key, StringComparison.Ordinal)))
        {
            return (item.Item, item.Name, false);
        }

        foreach (var item in side.Where(i => group.Contains(i.Key, StringComparer.Ordinal)))
        {
            return (item.Item, item.Name, true);
        }

        return (null, null, false);
    }
}
