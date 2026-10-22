using FerretSharp.Core.Data;
using FerretSharp.Core.Oracle;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Relations;

/// <summary>
/// Checks collected rows against the target before a copy (WP-42, ADR 0032). Tables of the source connection's schema go to
/// the target connection's schema (<c>ERP_DEV</c> → <c>ERP_TEST</c>), tables of other schemas keep their owner; columns match
/// by name; keys go over 1:1 (new keys in the target: backlog). Existing rows are looked up by key on the read path of the
/// target workspace – which sees what that workspace has written and not committed.
/// </summary>
/// <param name="sourceOwner">The schema the source connection browses (<see cref="SchemaCache.Owner"/>).</param>
/// <param name="target">The target connection's schema.</param>
/// <param name="targetData">The target workspace's read path.</param>
public sealed class SubgraphCopyPlanner(string sourceOwner, SchemaCache target, IDataAccess targetData)
{
    /// <summary>The target table of a source table (the source's own schema becomes the target's).</summary>
    public TableRef TargetOf(TableRef source) => source.Owner == sourceOwner ? source with { Owner = target.Owner } : source;

    public async Task<SubgraphCopyPlan> PlanAsync(RowGraph graph, CancellationToken cancellationToken)
    {
        var issues = new List<CopyIssue>();
        issues.AddRange(graph.Problems.Select(p => new CopyIssue(CopySeverity.Warning, p.Relation.From, p.Message)));

        var linked = graph.Links.Select(l => l.Relation).Distinct().ToList();
        var order = RelationGraph.Order(graph.Tables.Select(t => t.Table), linked);
        issues.AddRange(order.Cycles.Select(c => new CopyIssue(CopySeverity.Error, null, TextFormat.Format(RelationText.CopyCycle, c.Display))));

        var tables = new List<TableCopy>();
        foreach (var table in order.Tables)
        {
            tables.Add(await PlanTableAsync(graph, graph.Of(table)!, issues, cancellationToken));
        }

        return new SubgraphCopyPlan(tables, [.. issues.OrderBy(i => i.Severity)]);
    }

    private async Task<TableCopy> PlanTableAsync(RowGraph graph, TableRows source, List<CopyIssue> issues, CancellationToken cancellationToken)
    {
        var name = source.Table.Name;
        var rows = InsertOrder(graph, source, issues);
        TableCopy Without(string message)
        {
            issues.Add(new CopyIssue(CopySeverity.Error, source.Table, message));
            return new TableCopy(source, null, [], rows, new HashSet<RowKey>(), null, []);
        }

        if (source.Details.Table.Kind != TableKind.Table)
        {
            return Without(TextFormat.Format(RelationText.CopyNotATable, name));
        }

        var targetRef = TargetOf(source.Table);
        if (target.Find(targetRef) is not { } summary)
        {
            return Without(TextFormat.Format(RelationText.CopyTableMissing, name, targetRef));
        }

        if (summary.Kind != TableKind.Table)
        {
            return Without(TextFormat.Format(RelationText.CopyTargetNotATable, name, targetRef));
        }

        var details = await target.GetDetailsAsync(summary, cancellationToken);
        var columns = MapColumns(source, details, issues);
        var conflictKey = ConflictKeyOf(details, columns);
        if (conflictKey is null && !(details.PrimaryKeyEnforced && details.PrimaryKey.Count > 0) && details.UniqueKeys.Count == 0)
        {
            // A key whose columns get no value (an identity GENERATED ALWAYS) has its own issue.
            issues.Add(new CopyIssue(CopySeverity.Warning, source.Table, TextFormat.Format(RelationText.CopyNoConflictKey, name)));
        }

        var existing = conflictKey is null ? new HashSet<RowKey>() : await ExistingAsync(source, rows, details, columns, conflictKey, cancellationToken);
        var triggers = await target.GetTableTriggersAsync(details.Table.Ref, cancellationToken);
        if (triggers.Count > 0)
        {
            issues.Add(new CopyIssue(CopySeverity.Hint, source.Table,
                TextFormat.Format(RelationText.CopyTriggers, name, string.Join(", ", triggers.Select(t => t.Owner == details.Table.Owner ? t.Name : t.ToString())))));
        }

        return new TableCopy(source, details, columns, rows, existing, conflictKey, triggers);
    }

    /// <summary>
    /// Source columns to target columns by name. Not copied: virtual columns, columns the target lacks (a warning if the rows
    /// have values), types FerretSharp does not write, identity columns GENERATED ALWAYS (an error on a key column). A
    /// target column only the target has, NOT NULL without default, makes every insert fail.
    /// </summary>
    private static List<ColumnMapping> MapColumns(TableRows source, TableDetails target, List<CopyIssue> issues)
    {
        var name = source.Table.Name;
        var mapped = new List<ColumnMapping>();
        void Issue(CopySeverity severity, string text, params object?[] args) =>
            issues.Add(new CopyIssue(severity, source.Table, TextFormat.Format(text, [name, .. args])));

        for (var s = 0; s < source.Details.Columns.Count; s++)
        {
            var column = source.Details.Columns[s];
            if (column.IsVirtual)
            {
                continue;
            }

            var hasValues = source.Rows.Any(r => r.Values[s] is not null);
            var t = target.IndexOf(column.Name);
            if (t < 0)
            {
                if (hasValues)
                {
                    Issue(CopySeverity.Warning, RelationText.CopyColumnMissing, column.Name);
                }

                continue;
            }

            var targetColumn = target.Columns[t];
            if (targetColumn.IsVirtual)
            {
                continue;
            }

            if (targetColumn.IdentityAlways)
            {
                Issue(target.PrimaryKey.Contains(targetColumn.Name) ? CopySeverity.Error : CopySeverity.Warning,
                    target.PrimaryKey.Contains(targetColumn.Name) ? RelationText.CopyIdentityKey : RelationText.CopyIdentityValue, column.Name);
                continue;
            }

            if (!Copyable(column) || !Copyable(targetColumn))
            {
                if (hasValues)
                {
                    Issue(CopySeverity.Warning, RelationText.CopyColumnNotCopyable, column.Name, Copyable(column) ? targetColumn.DisplayType : column.DisplayType);
                }

                continue;
            }

            if (column.DisplayType != targetColumn.DisplayType)
            {
                Issue(CopySeverity.Warning, RelationText.CopyColumnType, column.Name, column.DisplayType, targetColumn.DisplayType);
            }

            mapped.Add(new ColumnMapping(s, t));
        }

        foreach (var only in target.Columns.Where(c => source.Details.IndexOf(c.Name) < 0
                                                       && !c.Nullable && c.Default is null && !c.IsIdentity && !c.IsVirtual))
        {
            Issue(CopySeverity.Error, RelationText.CopyColumnOnlyInTarget, only.Name);
        }

        return mapped;
    }

    /// <summary>Types written as values (grid editing, LOB editor); INTERVAL, BINARY_DOUBLE, LONG, XMLTYPE … are not.</summary>
    private static bool Copyable(ColumnInfo column) => OracleTypeMapper.IsEditableType(column) || OracleTypeMapper.IsLob(column);

    /// <summary>The target's enforced primary key, else its first unique key – if every column of it gets a value from the source.</summary>
    private static IReadOnlyList<int>? ConflictKeyOf(TableDetails target, IReadOnlyList<ColumnMapping> columns)
    {
        var filled = columns.Select(c => c.Target).ToHashSet();
        IEnumerable<IReadOnlyList<string>> keys = target.PrimaryKeyEnforced && target.PrimaryKey.Count > 0 ? [target.PrimaryKey, .. target.UniqueKeys] : target.UniqueKeys;
        return keys.Select(k => k.Select(target.IndexOf).ToList()).FirstOrDefault(k => k.Count > 0 && k.All(filled.Contains));
    }

    /// <summary>Source rows whose key (in the target's key columns) the target table holds already.</summary>
    private async Task<HashSet<RowKey>> ExistingAsync(
        TableRows source, IReadOnlyList<RowData> rows, TableDetails target, IReadOnlyList<ColumnMapping> columns, IReadOnlyList<int> key,
        CancellationToken cancellationToken)
    {
        var sourceOf = key.Select(t => columns.First(c => c.Target == t).Source).ToArray();
        var trim = key.Select(i => target.Columns[i]).Concat(sourceOf.Select(i => source.Details.Columns[i])).Any(c => OracleTypes.IsFixedChar(c.DataType));
        var byText = new Dictionary<string, List<RowKey>>(StringComparer.Ordinal);
        var keys = new List<IReadOnlyList<object>>();
        foreach (var row in rows)
        {
            if (sourceOf.Any(i => row.Values[i] is null))
            {
                continue; // a NULL in a unique key never conflicts
            }

            object[] values = [.. sourceOf.Select(i => trim && row.Values[i] is string s ? s.TrimEnd(' ') : row.Values[i]!)];
            var text = RowTraversal.KeyText(values, trim);
            if (!byText.TryGetValue(text, out var list))
            {
                byText[text] = list = [];
                keys.Add(values);
            }

            list.Add(row.Key);
        }

        var existing = new HashSet<RowKey>();
        if (keys.Count == 0)
        {
            return existing;
        }

        foreach (var found in await targetData.ReadByKeysAsync(target, key, keys, wholeLobs: false, cancellationToken))
        {
            var text = RowTraversal.KeyText([.. key.Select(i => trim && found.Values[i] is string s ? s.TrimEnd(' ') : found.Values[i]!)], trim);
            if (byText.TryGetValue(text, out var matching))
            {
                existing.UnionWith(matching);
            }
        }

        return existing;
    }

    /// <summary>
    /// The rows of a table, each after the rows of the same table it references (a manager before the staff); otherwise in
    /// the order found. Rows that reference each other in a circle cannot be inserted one after the other: an error.
    /// </summary>
    private static List<RowData> InsertOrder(RowGraph graph, TableRows table, List<CopyIssue> issues)
    {
        var self = graph.Links.Where(l => RelationGraph.IsSelfReference(l.Relation) && l.Relation.From == table.Table && !l.Child.Equals(l.Parent)).ToList();
        if (self.Count == 0)
        {
            return [.. table.Rows];
        }

        var waitingFor = table.Rows.ToDictionary(r => r.Key, r => self.Where(l => l.Child.Equals(r.Key)).Select(l => l.Parent).ToHashSet());
        var ordered = new List<RowData>(table.Rows.Count);
        var placed = new HashSet<RowKey>();
        while (ordered.Count < table.Rows.Count)
        {
            var ready = table.Rows.Where(r => !placed.Contains(r.Key) && waitingFor[r.Key].All(placed.Contains)).ToList();
            if (ready.Count == 0)
            {
                issues.Add(new CopyIssue(CopySeverity.Error, table.Table, TextFormat.Format(RelationText.CopyRowCycle, table.Table.Name)));
                ordered.AddRange(table.Rows.Where(r => !placed.Contains(r.Key)));
                break;
            }

            ordered.AddRange(ready);
            placed.UnionWith(ready.Select(r => r.Key));
        }

        return ordered;
    }
}
