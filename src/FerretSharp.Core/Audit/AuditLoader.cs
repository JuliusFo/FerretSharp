using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Audit;

/// <summary>Reads what the schema has of a table's audit objects, by the names the template gives them (WP-26).</summary>
public static class AuditLoader
{
    /// <summary>
    /// The audit table (only a table counts – a view of that name is a name taken), its comment and the trigger with its
    /// state and columns. Status and columns come fresh from the dictionary, not from the cached object list: a trigger
    /// turns invalid without the list noticing (a dropped column, ADR 0021).
    /// </summary>
    public static async Task<AuditState> LoadAsync(SchemaCache schema, AuditNames names, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(names);
        TableDetails? audit = null;
        string? comment = null;
        if (schema.Find(names.AuditTable) is { Kind: TableKind.Table, Synonym: null } table)
        {
            audit = await schema.GetDetailsAsync(table, cancellationToken);
            comment = (await schema.GetObjectInfoAsync(table, cancellationToken)).Comment;
        }

        AuditTriggerState? trigger = null;
        if (schema.FindPlSql(new PlSqlRef(names.AuditTable.Owner, names.Trigger, PlSqlKind.Trigger)) is { } unit)
        {
            var info = await schema.GetPlSqlInfoAsync(unit, cancellationToken);
            var columns = await schema.GetTriggerColumnsAsync(unit.Owner, unit.Name, cancellationToken);
            trigger = new AuditTriggerState(info.Trigger?.Table, info.Status == "VALID", info.Trigger?.Enabled ?? false, columns);
        }

        return new AuditState(audit, trigger, comment);
    }
}
