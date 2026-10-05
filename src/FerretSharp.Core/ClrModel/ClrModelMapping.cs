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

    private ClrModelMapping(ModelExport model, List<EntityMapping> entities, List<MappingIssue> issues)
    {
        Model = model;
        Entities = entities;
        Issues = issues;
        _byTable = entities.GroupBy(e => e.Table.Ref).ToDictionary(g => g.Key, g => g.ToList());
    }

    public ModelExport Model { get; }

    /// <summary>Entities found in the schema.</summary>
    public IReadOnlyList<EntityMapping> Entities { get; }

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
    /// letter case, reported) and every property to its column, and lists the differences.
    /// </summary>
    public static async Task<ClrModelMapping> BuildAsync(ModelExport model, SchemaCache schema, CancellationToken cancellationToken)
    {
        var entities = new List<EntityMapping>();
        var issues = new List<MappingIssue>();
        var defaultOwner = model.DefaultSchema ?? schema.Owner;

        var located = new List<(EntityExport Entity, TableSummary Table)>();
        foreach (var entity in model.Entities)
        {
            var name = entity.Table ?? entity.View;
            if (name is null)
            {
                continue; // keyless types on raw SQL, shared-type entities without a table: nothing to show
            }

            var owner = (entity.Table is not null ? entity.Schema : entity.ViewSchema) ?? defaultOwner;
            var table = Locate(schema, owner, name, defaultOwner);
            if (table is null)
            {
                var caseOnly = schema.Tables.FirstOrDefault(t => string.Equals(t.Owner, owner, StringComparison.OrdinalIgnoreCase)
                                                                 && string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
                if (caseOnly is null)
                {
                    issues.Add(new MappingIssue(MappingIssueKind.EntityWithoutTable, entity.Name, null, new TableRef(owner, name), null,
                        $"{ShortName(entity.ClrType)}: {(entity.Table is null ? "View" : "Tabelle")} {Qualified(owner, name, defaultOwner)} gibt es nicht."));
                    continue;
                }

                issues.Add(new MappingIssue(MappingIssueKind.CaseMismatch, entity.Name, null, caseOnly.Ref, null,
                    $"{ShortName(entity.ClrType)}: im Modell „{name}“, in der Datenbank „{caseOnly.Name}“ – EF Core quotet Namen, Abfragen fänden die Tabelle so nicht."));
                table = caseOnly;
            }

            located.Add((entity, table));
        }

        foreach (var group in located.GroupBy(l => l.Table.Ref))
        {
            var table = group.First().Table;
            var details = await schema.GetDetailsAsync(table, cancellationToken);
            var columns = details.Columns.Select(c => c.Name).ToList();
            var mapped = new HashSet<string>(StringComparer.Ordinal);

            foreach (var (entity, _) in group)
            {
                var properties = new Dictionary<string, PropertyExport>(StringComparer.Ordinal);
                foreach (var property in entity.Properties.Where(p => p.Column is not null))
                {
                    var column = columns.FirstOrDefault(c => c == property.Column);
                    if (column is null && columns.FirstOrDefault(c => string.Equals(c, property.Column, StringComparison.OrdinalIgnoreCase)) is { } caseOnly)
                    {
                        issues.Add(new MappingIssue(MappingIssueKind.CaseMismatch, entity.Name, property.Name, table.Ref, caseOnly,
                            $"{ShortName(entity.ClrType)}.{property.Name}: im Modell Spalte „{property.Column}“, in der Datenbank „{caseOnly}“."));
                        column = caseOnly;
                    }

                    if (column is null)
                    {
                        issues.Add(new MappingIssue(MappingIssueKind.PropertyWithoutColumn, entity.Name, property.Name, table.Ref, property.Column,
                            $"{ShortName(entity.ClrType)}.{property.Name}: Spalte {table.DisplayName}.{property.Column} gibt es nicht."));
                        continue;
                    }

                    properties.TryAdd(column, property);
                    mapped.Add(column);
                }

                entities.Add(new EntityMapping(entity, table, properties));
            }

            foreach (var column in columns.Where(c => !mapped.Contains(c)))
            {
                issues.Add(new MappingIssue(MappingIssueKind.ColumnWithoutProperty, group.First().Entity.Name, null, table.Ref, column,
                    $"{table.DisplayName}.{column}: keine Property in {string.Join(", ", group.Select(g => ShortName(g.Entity.ClrType)))}."));
            }
        }

        return new ClrModelMapping(model, entities, issues);
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
