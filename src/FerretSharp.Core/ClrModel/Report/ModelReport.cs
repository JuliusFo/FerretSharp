using FerretSharp.Core.Schema;

namespace FerretSharp.Core.ClrModel;

/// <summary>A table (or view) of the report's schemas that no entity maps.</summary>
/// <param name="Outgoing">Its declared foreign keys.</param>
/// <param name="Incoming">Declared foreign keys of other tables to it.</param>
/// <param name="Exception">The entry of the exceptions file that leaves it out; null if none.</param>
/// <param name="AuditOf">The base table if it is an audit table by the connection's template (left out, WP-63).</param>
public sealed record TableWithoutEntity(
    TableSummary Table, IReadOnlyList<ForeignKeyInfo> Outgoing, IReadOnlyList<ForeignKeyInfo> Incoming, ReportException? Exception, TableRef? AuditOf)
{
    /// <summary>Not counted as missing: meant to have no entity.</summary>
    public bool IsHidden => Exception is not null || AuditOf is not null;
}

/// <summary>A column of a mapped table that no property maps.</summary>
/// <param name="RequiredWithoutDefault">
/// NOT NULL without default, identity or computation on a table: an insert through EF fails as long as no property sets it.
/// </param>
public sealed record ColumnWithoutProperty(EntityMapping Entity, ColumnInfo Column, bool RequiredWithoutDefault);

/// <summary>A relationship of the C# model without a declared foreign key over the same columns.</summary>
/// <param name="ConstraintPossible">
/// Both ends are tables and the referenced columns are the primary key: a constraint could be created as it is (unique keys
/// are not loaded for the whole schema, so a reference to one is not marked).
/// </param>
public sealed record ModelOnlyRelation(ForeignKeyInfo Relation, bool ConstraintPossible);

public enum DbOnlyReason
{
    /// <summary>Both tables have entities, the model has no relationship between them over these columns.</summary>
    NoNavigation,

    /// <summary>The referenced table has no entity.</summary>
    TableWithoutEntity,
}

/// <summary>A declared foreign key of a mapped table that the C# model has no relationship for.</summary>
/// <param name="HasProperties">The entity has properties for all foreign key columns (only the navigation is missing).</param>
public sealed record DbOnlyRelation(ForeignKeyInfo ForeignKey, DbOnlyReason Reason, EntityMapping Dependent, bool HasProperties);

/// <summary>A mapped property no code of the solution uses outside tests and mapping (WP-40).</summary>
/// <param name="Located">Where the entity was found in the schema; null if it was not.</param>
/// <param name="UsedInTests">Only test code uses it.</param>
public sealed record UnusedProperty(EntityExport Entity, PropertyExport Property, EntityMapping? Located, bool UsedInTests);

/// <summary>
/// How the C# model and the database fit together (WP-72, ADR 0042): what the report page shows, from what is loaded already
/// – the model laid over the schema (<see cref="ClrModelMapping"/>, with the columns it read), the schema cache's tables,
/// declared keys and audit tables, and the exceptions file. No query of its own.
/// </summary>
public sealed class ModelReport
{
    private ModelReport(
        IReadOnlyList<TableWithoutEntity> tables, int tablesInScope, int tablesMapped, IReadOnlyList<ColumnWithoutProperty> columns,
        IReadOnlyList<MappingIssue> modelWithoutMatch, IReadOnlyList<ColumnMismatch> mismatches, IReadOnlyList<ModelOnlyRelation> modelOnly,
        IReadOnlyList<DbOnlyRelation> dbOnly, IReadOnlyList<string> owners)
    {
        Tables = tables;
        TablesInScope = tablesInScope;
        TablesMapped = tablesMapped;
        Columns = columns;
        ModelWithoutMatch = modelWithoutMatch;
        Mismatches = mismatches;
        ModelOnly = modelOnly;
        DbOnly = dbOnly;
        Owners = owners;
    }

    /// <summary>The schemas looked at: the connection's and every one the model maps a table in.</summary>
    public IReadOnlyList<string> Owners { get; }

    /// <summary>Tables and views without entity, hidden ones too (<see cref="TableWithoutEntity.IsHidden"/>), sorted by name.</summary>
    public IReadOnlyList<TableWithoutEntity> Tables { get; }

    /// <summary>Tables (not views) of <see cref="Owners"/> that count: mapped or missing, not hidden.</summary>
    public int TablesInScope { get; }

    /// <summary>Of <see cref="TablesInScope"/>, those an entity maps.</summary>
    public int TablesMapped { get; }

    /// <summary>Tables without entity that count as missing (not views, not hidden).</summary>
    public IEnumerable<TableWithoutEntity> MissingTables => Tables.Where(t => !t.IsHidden && t.Table.Kind == TableKind.Table);

    /// <summary>Columns without property, by table and column position.</summary>
    public IReadOnlyList<ColumnWithoutProperty> Columns { get; }

    /// <summary>Entities without table, properties without column and names in another letter case.</summary>
    public IReadOnlyList<MappingIssue> ModelWithoutMatch { get; }

    /// <summary>Properties whose column does not fit them, most severe first.</summary>
    public IReadOnlyList<ColumnMismatch> Mismatches { get; }

    /// <summary>Relationships only the model has, constraint-ready ones first.</summary>
    public IReadOnlyList<ModelOnlyRelation> ModelOnly { get; }

    /// <summary>Declared foreign keys of mapped tables the model has no relationship for.</summary>
    public IReadOnlyList<DbOnlyRelation> DbOnly { get; }

    public static ModelReport Build(ClrModelMapping mapping, SchemaCache schema, ReportExceptions exceptions)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(exceptions);

        var owners = mapping.Entities.Select(e => e.Table.Owner).Prepend(schema.Owner).Distinct(StringComparer.Ordinal).ToList();
        var ownerSet = owners.ToHashSet(StringComparer.Ordinal);
        var declared = schema.ForeignKeys.Where(f => f is { Source: FkSource.Declared, Audit: null }).ToList();
        var outgoing = declared.ToLookup(f => f.From);
        var incoming = declared.ToLookup(f => f.To);

        var tables = new List<TableWithoutEntity>();
        var inScope = 0;
        var mapped = 0;
        foreach (var table in schema.Tables.Where(t => ownerSet.Contains(t.Owner)))
        {
            if (mapping.EntityOf(table.Ref) is not null)
            {
                if (table.Kind == TableKind.Table)
                {
                    inScope++;
                    mapped++;
                }

                continue;
            }

            var row = new TableWithoutEntity(table, [.. outgoing[table.Ref]], [.. incoming[table.Ref]], exceptions.Match(table.Name), schema.AuditBaseOf(table.Ref));
            tables.Add(row);
            if (table.Kind == TableKind.Table && !row.IsHidden)
            {
                inScope++;
            }
        }

        tables.Sort((a, b) => string.Compare(a.Table.DisplayName, b.Table.DisplayName, StringComparison.Ordinal));
        var hidden = tables.Where(t => t.IsHidden).Select(t => t.Table.Ref).ToHashSet();

        return new ModelReport(
            tables, inScope, mapped, ColumnsWithoutProperty(mapping),
            [.. mapping.Issues.Where(i => i.Kind is MappingIssueKind.EntityWithoutTable or MappingIssueKind.PropertyWithoutColumn or MappingIssueKind.CaseMismatch)],
            mapping.ColumnMismatches, ModelOnlyRelations(mapping, schema, declared), DbOnlyRelations(mapping, declared, hidden), owners);
    }

    private static List<ColumnWithoutProperty> ColumnsWithoutProperty(ClrModelMapping mapping)
    {
        var result = new List<ColumnWithoutProperty>();
        foreach (var issue in mapping.Issues.Where(i => i is { Kind: MappingIssueKind.ColumnWithoutProperty, Table: not null, Column: not null }))
        {
            if (mapping.EntityOf(issue.Table!) is not { } entity
                || mapping.ColumnsOf(issue.Table!).FirstOrDefault(c => c.Name == issue.Column) is not { } column)
            {
                continue;
            }

            var required = entity.Table.Kind == TableKind.Table && !column.Nullable && string.IsNullOrWhiteSpace(column.Default)
                           && !column.IsIdentity && !column.IsVirtual;
            result.Add(new ColumnWithoutProperty(entity, column, required));
        }

        return [.. result.OrderBy(c => c.Entity.Table.DisplayName, StringComparer.Ordinal).ThenBy(c => c.Column.Position)];
    }

    private static List<ModelOnlyRelation> ModelOnlyRelations(ClrModelMapping mapping, SchemaCache schema, List<ForeignKeyInfo> declared)
    {
        var covered = declared.Select(SchemaCache.PairsOf).ToHashSet(StringComparer.Ordinal);
        var result = new List<ModelOnlyRelation>();
        foreach (var relation in mapping.ForeignKeys.Where(f => !covered.Contains(SchemaCache.PairsOf(f))))
        {
            var key = schema.PrimaryKeyOf(relation.To);
            var possible = schema.Find(relation.From)?.Kind == TableKind.Table && schema.Find(relation.To)?.Kind == TableKind.Table
                           && key.Count > 0 && key.Count == relation.ToColumns.Count && !key.Except(relation.ToColumns, StringComparer.Ordinal).Any();
            result.Add(new ModelOnlyRelation(relation, possible));
        }

        return [.. result.OrderByDescending(r => r.ConstraintPossible).ThenBy(r => r.Relation.From.Name, StringComparer.Ordinal).ThenBy(r => r.Relation.Name, StringComparer.Ordinal)];
    }

    private static List<DbOnlyRelation> DbOnlyRelations(ClrModelMapping mapping, List<ForeignKeyInfo> declared, HashSet<TableRef> hidden)
    {
        var modelled = mapping.ForeignKeys.Select(SchemaCache.PairsOf).ToHashSet(StringComparer.Ordinal);
        var result = new List<DbOnlyRelation>();
        foreach (var fk in declared)
        {
            // A foreign key of a table without entity is part of that table's row; one to a table meant to have none is expected.
            if (mapping.EntityOf(fk.From) is not { } dependent || hidden.Contains(fk.To) || modelled.Contains(SchemaCache.PairsOf(fk)))
            {
                continue;
            }

            var reason = mapping.EntityOf(fk.To) is null ? DbOnlyReason.TableWithoutEntity : DbOnlyReason.NoNavigation;
            var hasProperties = fk.FromColumns.All(c => mapping.PropertyOf(fk.From, c) is not null);
            result.Add(new DbOnlyRelation(fk, reason, dependent, hasProperties));
        }

        return [.. result.OrderBy(r => r.Reason).ThenBy(r => r.ForeignKey.From.Name, StringComparer.Ordinal).ThenBy(r => r.ForeignKey.Name, StringComparer.Ordinal)];
    }

    /// <summary>
    /// The mapped properties no code uses outside tests and mapping (<see cref="CodeUsageIndex.Unused"/>), sorted by entity and
    /// property, with where each entity was found and whether tests use it.
    /// </summary>
    public static IReadOnlyList<UnusedProperty> Unused(ClrModelMapping mapping, CodeUsageIndex index)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(index);
        var located = mapping.Entities.GroupBy(m => m.Entity).ToDictionary(g => g.Key, g => g.First());
        return [.. index.Unused()
            .Select(u => new UnusedProperty(u.Entity, u.Property, located.GetValueOrDefault(u.Entity), index.IsUsedInTests(u.Entity, u.Property)))
            .OrderBy(u => ClrModelMapping.ShortName(u.Entity.ClrType), StringComparer.Ordinal)
            .ThenBy(u => u.Property.Name, StringComparer.Ordinal)];
    }
}
