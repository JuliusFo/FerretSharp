using FerretSharp.Core.Resources;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.ClrModel;

public enum MappingIssueKind
{
    /// <summary>The entity's table or view is not in the schema (not created yet, dropped, other schema).</summary>
    EntityWithoutTable,

    /// <summary>A property's column is not in the table.</summary>
    PropertyWithoutColumn,

    /// <summary>A column of a mapped table that no property maps.</summary>
    ColumnWithoutProperty,

    /// <summary>
    /// Table or column exists only in another letter case. EF Core quotes identifiers on Oracle, so the project's queries
    /// would not find it; FerretSharp still matches it for the display.
    /// </summary>
    CaseMismatch,
}

/// <param name="Entity">Entity name (CLR full name) if the issue belongs to one.</param>
public sealed record MappingIssue(MappingIssueKind Kind, string? Entity, string? Property, TableRef? Table, string? Column, string Message);

/// <summary>An entity and the table (or view) it maps to; <see cref="Properties"/> by column name.</summary>
public sealed record EntityMapping(EntityExport Entity, TableSummary Table, IReadOnlyDictionary<string, PropertyExport> Properties);

/// <summary>
/// The C# model laid over the database (the annotation layer, CLAUDE.md section 2): which entity and property belong to
/// a table and column, and where model and database differ. The schema records stay as they are; this sits beside them,
/// addressed by (owner, table, column).
/// </summary>
public sealed class ClrModelMapping
{
    private readonly Dictionary<TableRef, List<EntityMapping>> _byTable;

    private readonly ILookup<(TableRef Table, string Column), ColumnMismatch> _mismatchesByColumn;

    private ClrModelMapping(ModelExport model, List<EntityMapping> entities, List<MappingIssue> issues, List<ColumnMismatch> mismatches)
    {
        Model = model;
        Entities = entities;
        Issues = issues;
        ColumnMismatches = mismatches;
        _byTable = entities.GroupBy(e => e.Table.Ref).ToDictionary(g => g.Key, g => g.ToList());
        _mismatchesByColumn = mismatches.ToLookup(m => (m.Table.Ref, m.Column.Name));
        ForeignKeys = RelationshipsOf(entities);
    }

    /// <summary>Properties whose column does not fit them (type, NULL, length, precision), most severe first.</summary>
    public IReadOnlyList<ColumnMismatch> ColumnMismatches { get; }

    /// <summary>The mismatches of one column (several entities may share it).</summary>
    public IEnumerable<ColumnMismatch> MismatchesOf(TableRef table, string column) => _mismatchesByColumn[(table, column)];

    /// <summary>
    /// The model's relationships as foreign keys between tables (<see cref="FkSource.ClrModel"/>), named after the
    /// navigation (<c>Auftrag.Kunde</c>). Whether the database declares them too is up to <see cref="SchemaCache"/>.
    /// </summary>
    public IReadOnlyList<ForeignKeyInfo> ForeignKeys { get; }

    public ModelExport Model { get; }

    /// <summary>Entities found in the schema – one entry per table or view (an entity on both has two).</summary>
    public IReadOnlyList<EntityMapping> Entities { get; }

    /// <summary>How many entities were found in the schema (each counted once).</summary>
    public int MappedEntityCount => Entities.Select(e => e.Entity).Distinct().Count();

    public IReadOnlyList<MappingIssue> Issues { get; }

    /// <summary>The entity of a table: the owning one if several share it (owned types, table splitting).</summary>
    public EntityMapping? EntityOf(TableRef table) =>
        _byTable.TryGetValue(table, out var entities) ? entities.FirstOrDefault(e => !e.Entity.IsOwned) ?? entities[0] : null;

    public PropertyExport? PropertyOf(TableRef table, string column) =>
        _byTable.TryGetValue(table, out var entities)
            ? entities.Select(e => e.Properties.GetValueOrDefault(column)).FirstOrDefault(p => p is not null)
            : null;

    /// <summary>
    /// Matches every entity to its table or view (exact name; a synonym of the default schema; otherwise in another
    /// letter case, reported) and every property to its column, and lists the differences – also of type, NULL, length
    /// and precision (<see cref="ColumnTypeCheck"/>).
    /// </summary>
    /// <param name="progress">The current step, for the model page.</param>
    public static async Task<ClrModelMapping> BuildAsync(
        ModelExport model, SchemaCache schema, CancellationToken cancellationToken, IProgress<string>? progress = null)
    {
        var entities = new List<EntityMapping>();
        var issues = new List<MappingIssue>();
        var defaultOwner = model.DefaultSchema ?? schema.Owner;

        var located = new List<(EntityExport Entity, TableSummary Table, Func<PropertyExport, string?> ColumnOf)>();
        foreach (var entity in model.Entities)
        {
            // Queries use the view if the entity has one; a table beside it serves SaveChanges only. Conventions often give
            // every entity a table name, also those on views – that table not existing is no difference worth reporting.
            var candidates = new List<(string Name, string Owner, bool IsView, Func<PropertyExport, string?> ColumnOf)>();
            if (entity.View is { } viewName)
            {
                candidates.Add((viewName, entity.ViewSchema ?? defaultOwner, true, p => p.ViewColumn ?? p.Column));
            }

            if (entity.Table is { } tableName)
            {
                candidates.Add((tableName, entity.Schema ?? defaultOwner, false, p => p.Column));
            }

            if (candidates.Count == 0)
            {
                continue; // keyless types on raw SQL, shared-type entities without a table: nothing to show
            }

            var found = 0;
            foreach (var candidate in candidates)
            {
                if (Locate(schema, candidate.Owner, candidate.Name, defaultOwner) is { } exact)
                {
                    located.Add((entity, exact, candidate.ColumnOf));
                    found++;
                }
            }

            if (found > 0)
            {
                continue;
            }

            var (name, owner, isView, columnOf) = candidates[0];
            var caseOnly = schema.Tables.FirstOrDefault(t => string.Equals(t.Owner, owner, StringComparison.OrdinalIgnoreCase)
                                                             && string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
            if (caseOnly is null)
            {
                issues.Add(new MappingIssue(MappingIssueKind.EntityWithoutTable, entity.Name, null, new TableRef(owner, name), null,
                    TextFormat.Format(isView ? ClrModelText.EntityWithoutView : ClrModelText.EntityWithoutTable,
                        ShortName(entity.ClrType), Qualified(owner, name, defaultOwner))));
                continue;
            }

            issues.Add(new MappingIssue(MappingIssueKind.CaseMismatch, entity.Name, null, caseOnly.Ref, null,
                TextFormat.Format(isView ? ClrModelText.ViewCaseMismatch : ClrModelText.TableCaseMismatch, ShortName(entity.ClrType), name, caseOnly.Name)));
            located.Add((entity, caseOnly, columnOf));
        }

        // One query per schema for all columns: one per table took minutes for a large model over a VPN.
        var columnsByOwner = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<ColumnInfo>>>(StringComparer.Ordinal);
        foreach (var owner in located.Select(l => l.Table.Owner).Distinct(StringComparer.Ordinal))
        {
            progress?.Report(TextFormat.Format(ClrModelText.StepReadColumns, owner));
            columnsByOwner[owner] = await schema.GetColumnsAsync(owner, cancellationToken);
        }

        progress?.Report(ClrModelText.StepCompare);
        var mismatches = new List<ColumnMismatch>();
        foreach (var group in located.GroupBy(l => l.Table.Ref))
        {
            var table = group.First().Table;
            var columnInfos = columnsByOwner[table.Owner].GetValueOrDefault(table.Name) ?? [];
            var columns = columnInfos.Select(c => c.Name).ToList();
            var mapped = new HashSet<string>(StringComparer.Ordinal);

            foreach (var (entity, _, columnOf) in group)
            {
                var properties = new Dictionary<string, PropertyExport>(StringComparer.Ordinal);
                foreach (var property in entity.Properties)
                {
                    if (columnOf(property) is not { } modelColumn)
                    {
                        continue;
                    }

                    var column = columns.FirstOrDefault(c => c == modelColumn);
                    if (column is null && columns.FirstOrDefault(c => string.Equals(c, modelColumn, StringComparison.OrdinalIgnoreCase)) is { } caseOnly)
                    {
                        issues.Add(new MappingIssue(MappingIssueKind.CaseMismatch, entity.Name, property.Name, table.Ref, caseOnly,
                            TextFormat.Format(ClrModelText.ColumnCaseMismatch, ShortName(entity.ClrType), property.Name, modelColumn, caseOnly)));
                        column = caseOnly;
                    }

                    if (column is null)
                    {
                        issues.Add(new MappingIssue(MappingIssueKind.PropertyWithoutColumn, entity.Name, property.Name, table.Ref, modelColumn,
                            TextFormat.Format(ClrModelText.PropertyWithoutColumn, ShortName(entity.ClrType), property.Name, table.DisplayName, modelColumn)));
                        continue;
                    }

                    properties.TryAdd(column, property);
                    mapped.Add(column);
                    // Views and materialized views are only read; SaveChanges writes to the entity's table.
                    mismatches.AddRange(ColumnTypeCheck.Check(entity, property, table, columnInfos.First(c => c.Name == column),
                        readOnly: table.Kind != TableKind.Table));
                }

                entities.Add(new EntityMapping(entity, table, properties));
            }

            foreach (var column in columns.Where(c => !mapped.Contains(c)))
            {
                issues.Add(new MappingIssue(MappingIssueKind.ColumnWithoutProperty, group.First().Entity.Name, null, table.Ref, column,
                    TextFormat.Format(ClrModelText.ColumnWithoutProperty, table.DisplayName, column, string.Join(", ", group.Select(g => ShortName(g.Entity.ClrType))))));
            }
        }

        var ordered = mismatches
            .OrderBy(m => m.Severity)
            .ThenBy(m => ShortName(m.Entity.ClrType), StringComparer.Ordinal)
            .ThenBy(m => m.Column.Position)
            .ToList();
        return new ClrModelMapping(model, entities, issues, ordered);
    }

    /// <summary>
    /// Every model foreign key whose properties all map to columns, from each table or view of the dependent entity to
    /// the principal's table (its view if it has no table). Left out: links of a table to itself over the same columns
    /// (owned types and table splitting share the owner's table and key) and duplicates (derived types sharing a table).
    /// </summary>
    private static List<ForeignKeyInfo> RelationshipsOf(List<EntityMapping> entities)
    {
        var byName = entities.ToLookup(e => e.Entity.Name, StringComparer.Ordinal);
        var result = new List<ForeignKeyInfo>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dependent in entities)
        {
            foreach (var fk in dependent.Entity.ForeignKeys)
            {
                var principal = byName[fk.PrincipalEntity].OrderBy(e => e.Table.Kind == TableKind.Table ? 0 : 1).FirstOrDefault();
                if (principal is null
                    || Columns(dependent, fk.Properties) is not { } from
                    || Columns(principal, fk.PrincipalProperties) is not { } to
                    || (dependent.Table.Ref == principal.Table.Ref && from.SequenceEqual(to, StringComparer.Ordinal)))
                {
                    continue;
                }

                if (seen.Add($"{dependent.Table.Ref}\0{principal.Table.Ref}\0{string.Join('\0', from)}\0{string.Join('\0', to)}"))
                {
                    var name = $"{ShortName(dependent.Entity.ClrType)}.{fk.Navigation ?? string.Join("+", fk.Properties)}";
                    result.Add(new ForeignKeyInfo(name, dependent.Table.Ref, from, principal.Table.Ref, to, FkSource.ClrModel));
                }
            }
        }

        return result;
    }

    /// <summary>The columns of the given properties of an entity, or null if one of them has no column.</summary>
    private static List<string>? Columns(EntityMapping entity, IReadOnlyList<string> properties)
    {
        var columns = new List<string>(properties.Count);
        foreach (var property in properties)
        {
            var column = entity.Properties.FirstOrDefault(p => p.Value.Name == property).Key;
            if (column is null)
            {
                return null;
            }

            columns.Add(column);
        }

        return columns.Count > 0 ? columns : null;
    }

    /// <summary>
    /// The object an unqualified name means in the default schema: own table or view, otherwise a synonym (private
    /// before public, as Oracle resolves it). A qualified name in another schema must exist there.
    /// </summary>
    private static TableSummary? Locate(SchemaCache schema, string owner, string name, string defaultOwner)
    {
        if (schema.Find(new TableRef(owner, name)) is { } own && own.Synonym is null)
        {
            return own;
        }

        if (owner == defaultOwner && schema.Tables.FirstOrDefault(t => t.Synonym?.Name == name) is { } synonym)
        {
            return synonym;
        }

        return schema.Tables.FirstOrDefault(t => t.Owner == owner && t.Name == name);
    }

    private static string Qualified(string owner, string name, string defaultOwner) => owner == defaultOwner ? name : $"{owner}.{name}";

    /// <summary><c>Shop.Entities.Kunde</c> → <c>Kunde</c>.</summary>
    public static string ShortName(string clrType)
    {
        var generic = clrType.IndexOf('`', StringComparison.Ordinal);
        var name = generic >= 0 ? clrType[..generic] : clrType;
        return name[(name.LastIndexOfAny(['.', '+']) + 1)..];
    }
}
