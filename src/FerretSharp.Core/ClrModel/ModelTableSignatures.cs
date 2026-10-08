using System.Text.Json;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.ClrModel;

/// <summary>
/// What a loaded model says about each table – its entities with properties, value mappings and relationships, and which
/// columns they map – as text to compare. After a build the whole model is loaded again, but usually only a few tables
/// read differently; only their views need to follow (ADR 0016).
/// </summary>
public sealed class ModelTableSignatures
{
    public static readonly ModelTableSignatures Empty = new(new Dictionary<TableRef, string>(), new Dictionary<TableRef, string>());

    private readonly IReadOnlyDictionary<TableRef, string> _tables;
    private readonly IReadOnlyDictionary<TableRef, string> _entityNames;

    private ModelTableSignatures(IReadOnlyDictionary<TableRef, string> tables, IReadOnlyDictionary<TableRef, string> entityNames)
    {
        _tables = tables;
        _entityNames = entityNames;
    }

    public static ModelTableSignatures Of(ClrModelMapping? mapping)
    {
        if (mapping is null)
        {
            return Empty;
        }

        var tables = mapping.Entities
            .GroupBy(e => e.Table.Ref)
            .ToDictionary(g => g.Key, g => JsonSerializer.Serialize(
                g.Select(e => new { e.Entity, Columns = e.Properties.Keys.Order(StringComparer.Ordinal).ToList() }).ToList(),
                ModelHostResult.JsonOptions));
        var names = tables.Keys.ToDictionary(t => t, t => ClrModelMapping.ShortName(mapping.EntityOf(t)!.Entity.ClrType));
        return new ModelTableSignatures(tables, names);
    }

    /// <summary>The tables that read differently in <paramref name="other"/> (also those mapped in only one of them).</summary>
    public IReadOnlySet<TableRef> ChangedTables(ModelTableSignatures other) =>
        _tables.Keys.Union(other._tables.Keys)
            .Where(t => !_tables.TryGetValue(t, out var a) || !other._tables.TryGetValue(t, out var b) || a != b)
            .ToHashSet();

    /// <summary>Whether any table has another entity name beside it (or one more or less).</summary>
    public bool SameEntityNames(ModelTableSignatures other) =>
        _entityNames.Count == other._entityNames.Count
        && _entityNames.All(e => other._entityNames.TryGetValue(e.Key, out var name) && name == e.Value);
}
