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
    /// <summary>Columns a warning names at most.</summary>
    private const int MaxNamedColumns = 5;

    /// <summary>The target table of a source table (the source's own schema becomes the target's).</summary>
    public TableRef TargetOf(TableRef source) => source.Owner == sourceOwner ? source with { Owner = target.Owner } : source;

    public Task<SubgraphCopyPlan> PlanAsync(RowGraph graph, CancellationToken cancellationToken) => PlanAsync(graph, [], cancellationToken);

    /// <param name="skippedParents">
    /// Parent relationships the user switched off: the rows they reference are looked up in the target – missing ones are an
    /// error where the target has the constraint, a warning otherwise.
    /// </param>
    public async Task<SubgraphCopyPlan> PlanAsync(RowGraph graph, IReadOnlyCollection<ForeignKeyInfo> skippedParents, CancellationToken cancellationToken)
    {
        var issues = new List<CopyIssue>();
        issues.AddRange(graph.Problems.Select(p => new CopyIssue(CopySeverity.Warning, p.Relation.From, p.Message)));

        // Switched-off parents first: those that come along by another way bind the order like a followed relationship.
        var skippedIssues = new List<CopyIssue>();
        var binding = graph.RelationsLinked.ToHashSet();
        foreach (var relation in skippedParents)
        {
            if (await CheckSkippedParentAsync(graph, relation, skippedIssues, cancellationToken))
            {
                binding.Add(relation);
            }
        }

        var order = RelationGraph.Order(graph.Tables.Select(t => t.Table), binding);
        issues.AddRange(order.Cycles.Select(c => new CopyIssue(CopySeverity.Error, null, TextFormat.Format(RelationText.CopyCycle, c.Display))));

        var tables = new List<TableCopy>();
        foreach (var table in order.Tables)
        {
            tables.Add(await PlanTableAsync(graph, graph.Of(table)!, issues, cancellationToken));
        }

        issues.AddRange(skippedIssues);
        return new SubgraphCopyPlan(tables, [.. issues.OrderBy(i => i.Severity)]);
    }

    /// <summary>
    /// The rows a switched-off relationship references from the copied rows must be in the target already – or come along
    /// with the copy by another way. What is missing is counted by distinct key.
    /// </summary>
    /// <returns>
    /// Whether copied rows reference rows that come along by another way: then their table has to be inserted first, although
    /// the traversal did not link them along this relationship.
    /// </returns>
    private async Task<bool> CheckSkippedParentAsync(RowGraph graph, ForeignKeyInfo relation, List<CopyIssue> issues, CancellationToken cancellationToken)
    {
        if (graph.Of(relation.From) is not { } children)
        {
            return false;
        }

        var from = relation.FromColumns.Select(children.Details.IndexOf).ToArray();
        if (from.Any(i => i < 0))
        {
            return false;
        }

        var trim = from.Any(i => OracleTypes.IsFixedChar(children.Details.Columns[i].DataType));
        var copied = graph.Of(relation.To) is { } parents && relation.ToColumns.Select(parents.Details.IndexOf).ToArray() is var to && to.All(i => i >= 0)
            ? parents.Rows.Select(r => RowTraversal.KeyText([.. to.Select(i => r.Values[i] ?? "")], true)).ToHashSet(StringComparer.Ordinal)
            : [];
        var wanted = new Dictionary<string, IReadOnlyList<object>>(StringComparer.Ordinal);
        var comesAlong = false;
        foreach (var row in children.Rows)
        {
            if (from.Any(i => row.Values[i] is null))
            {
                continue;
            }

            object[] values = [.. from.Select(i => trim && row.Values[i] is string s ? s.TrimEnd(' ') : row.Values[i]!)];
            var text = RowTraversal.KeyText(values, true);
            if (copied.Contains(text))
            {
                comesAlong = true;
            }
            else
            {
                wanted.TryAdd(text, values);
            }
        }

        if (wanted.Count == 0 || target.Find(TargetOf(relation.To)) is not { } parentSummary)
        {
            return comesAlong;
        }

        var parentDetails = await target.GetDetailsAsync(parentSummary, cancellationToken);
        var key = relation.ToColumns.Select(parentDetails.IndexOf).ToArray();
        if (key.Any(i => i < 0))
        {
            return comesAlong;
        }

        foreach (var found in await targetData.ReadByKeysAsync(parentDetails, key, [.. wanted.Values], wholeLobs: false, maxRows: null, cancellationToken))
        {
            wanted.Remove(RowTraversal.KeyText([.. key.Select(i => found.Values[i] ?? "")], true));
        }

        if (wanted.Count == 0)
        {
            return comesAlong;
        }

        var pairs = relation.FromColumns.Zip(relation.ToColumns).Order().ToList();
        var declared = target.OutgoingOf(TargetOf(relation.From)).Any(fk => fk.Source == FkSource.Declared && fk.To == parentSummary.Ref
                                                                           && fk.FromColumns.Zip(fk.ToColumns).Order().SequenceEqual(pairs));
        issues.Add(new CopyIssue(declared ? CopySeverity.Error : CopySeverity.Warning, relation.From, TextFormat.Plural(wanted.Count,
            declared ? RelationText.CopyParentMissingOne : RelationText.CopyParentMissingUncheckedOne,
            declared ? RelationText.CopyParentMissingOther : RelationText.CopyParentMissingUncheckedOther,
            relation.From.Name, relation.To.Name, relation.Name)));
        return comesAlong;
    }

    private async Task<TableCopy> PlanTableAsync(RowGraph graph, TableRows source, List<CopyIssue> issues, CancellationToken cancellationToken)
    {
        var name = source.Table.Name;
        var rows = InsertOrder(graph, source, issues);
        TableCopy Without(string message)
        {
            issues.Add(new CopyIssue(CopySeverity.Error, source.Table, message));
            return new TableCopy(source, null, [], rows, new HashSet<RowKey>(), new HashSet<RowKey>(), null, []);
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
        var columns = Conversions(source, details, MapColumns(source, details, issues), issues);
        var conflictKey = ConflictKeyOf(details, columns);
        if (conflictKey is null && !(details.PrimaryKeyEnforced && details.PrimaryKey.Count > 0) && details.UniqueKeys.Count == 0)
        {
            // A key whose columns get no value (an identity GENERATED ALWAYS) has its own issue.
            issues.Add(new CopyIssue(CopySeverity.Warning, source.Table, TextFormat.Format(RelationText.CopyNoConflictKey, name)));
        }

        var matched = conflictKey is null ? [] : await MatchAsync(source, rows, details, columns, conflictKey, cancellationToken);
        var existing = matched.Keys.ToHashSet();

        // Same key, other values: with keys from sequences, ID 4711 in the source may be another record in the target – the
        // copied children would then hang on the wrong parent.
        // The columns are named: some differ between any two databases (ERSTELLT_AM DEFAULT SYSDATE) and say nothing (E2E).
        var differences = matched.Select(m => (m.Key, Columns: DifferingColumns(source.Details, m.Value.Source, details, m.Value.Target, columns, conflictKey!).ToList()))
            .Where(d => d.Columns.Count > 0).ToList();
        var differing = differences.Select(d => d.Key).ToHashSet();
        if (differing.Count > 0)
        {
            var named = differences.SelectMany(d => d.Columns).Distinct().Order().Select(i => details.Columns[i].Name).ToList();
            var list = string.Join(", ", named.Take(MaxNamedColumns)) + (named.Count > MaxNamedColumns ? ", …" : "");
            issues.Add(new CopyIssue(CopySeverity.Warning, source.Table,
                TextFormat.Plural(differing.Count, RelationText.CopyExistingDifferOne, RelationText.CopyExistingDifferOther, name, list)));
        }

        // A new row whose other unique key the target holds already: the same record under another ID, or ORA-00001.
        foreach (var key in KeysOf(details).Where(k => conflictKey is null || !k.Order().SequenceEqual(conflictKey.Order())))
        {
            var fresh = rows.Where(r => !existing.Contains(r.Key)).ToList();
            if (fresh.Count == 0 || !key.All(columns.Select(c => c.Target).Contains))
            {
                continue;
            }

            var taken = await MatchAsync(source, fresh, details, columns, key, cancellationToken);
            if (taken.Count > 0)
            {
                issues.Add(new CopyIssue(CopySeverity.Error, source.Table, TextFormat.Plural(taken.Count, RelationText.CopyUniqueTakenOne,
                    RelationText.CopyUniqueTakenOther, name, string.Join(", ", key.Select(i => details.Columns[i].Name)))));
            }
        }

        var triggers = await target.GetTableTriggersAsync(details.Table.Ref, cancellationToken);
        if (triggers.Count > 0)
        {
            issues.Add(new CopyIssue(CopySeverity.Hint, source.Table,
                TextFormat.Format(RelationText.CopyTriggers, name, string.Join(", ", triggers.Select(t => t.Owner == details.Table.Owner ? t.Name : t.ToString())))));
        }

        return new TableCopy(source, details, columns, rows, existing, differing, conflictKey, triggers);
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

    /// <summary>
    /// The columns whose source values lack the target's value type (NUMBER into VARCHAR2), marked to be converted
    /// (<see cref="ColumnMapping.Convert"/>); values that do not convert (text into NUMBER) are an error.
    /// </summary>
    private static List<ColumnMapping> Conversions(TableRows source, TableDetails target, List<ColumnMapping> columns, List<CopyIssue> issues) =>
    [
        .. columns.Select(c =>
        {
            var (from, to) = (source.Details.Columns[c.Source], target.Columns[c.Target]);
            var foreign = source.Rows.Select(r => r.Values[c.Source]).Where(v => v is not null && !ColumnMapping.Fits(to, v)).ToList();
            if (foreign.Count == 0)
            {
                return c;
            }

            var failed = foreign.Count(v => !ColumnMapping.Converted(from, to, v).IsValid);
            if (failed > 0)
            {
                issues.Add(new CopyIssue(CopySeverity.Error, source.Table, TextFormat.Plural(failed,
                    RelationText.CopyValueNotConvertibleOne, RelationText.CopyValueNotConvertibleOther, source.Table.Name, to.Name, to.DisplayType)));
            }

            return c with { Convert = true };
        }),
    ];

    /// <summary>Types written as values (grid editing, LOB editor); INTERVAL, BINARY_DOUBLE, LONG, XMLTYPE … are not.</summary>
    private static bool Copyable(ColumnInfo column) => OracleTypeMapper.IsEditableType(column) || OracleTypeMapper.IsLob(column);

    /// <summary>The target's enforced primary key, else its first unique key – if every column of it gets a value from the source.</summary>
    private static IReadOnlyList<int>? ConflictKeyOf(TableDetails target, IReadOnlyList<ColumnMapping> columns)
    {
        var filled = columns.Select(c => c.Target).ToHashSet();
        return KeysOf(target).FirstOrDefault(k => k.All(filled.Contains));
    }

    /// <summary>The target's keys as column indexes: the enforced primary key first, then the unique keys.</summary>
    private static IEnumerable<IReadOnlyList<int>> KeysOf(TableDetails target)
    {
        IEnumerable<IReadOnlyList<string>> keys = target.PrimaryKeyEnforced && target.PrimaryKey.Count > 0 ? [target.PrimaryKey, .. target.UniqueKeys] : target.UniqueKeys;
        return keys.Where(k => k.Count > 0 && k.All(c => target.IndexOf(c) >= 0)).Select(k => (IReadOnlyList<int>)[.. k.Select(target.IndexOf)]);
    }

    /// <summary>
    /// The copied columns (by target index) in which a source row and the target row with its key hold other values – keys and
    /// LOBs left out (the target's LOBs are read as preview only). Compared as the traversal compares keys: 5 and 5.00 are
    /// equal, CHAR with and without its padding too.
    /// </summary>
    private static IEnumerable<int> DifferingColumns(
        TableDetails source, RowData sourceRow, TableDetails target, RowData targetRow, IReadOnlyList<ColumnMapping> columns, IReadOnlyList<int> key) =>
        columns.Where(c => !key.Contains(c.Target) && !OracleTypeMapper.IsLob(target.Columns[c.Target])).Where(c =>
        {
            var (a, b) = (c.ValueOf(source, target, sourceRow), targetRow.Values[c.Target]);
            if (a is null || b is null)
            {
                return a is not null || b is not null;
            }

            var trim = OracleTypes.IsFixedChar(source.Columns[c.Source].DataType) || OracleTypes.IsFixedChar(target.Columns[c.Target].DataType);
            return RowTraversal.KeyText([a], trim) != RowTraversal.KeyText([b], trim);
        }).Select(c => c.Target);

    /// <summary>Source rows whose key (in the target's key columns <paramref name="key"/>) the target table holds, with the target's row.</summary>
    private async Task<Dictionary<RowKey, (RowData Source, RowData Target)>> MatchAsync(
        TableRows source, IReadOnlyList<RowData> rows, TableDetails target, IReadOnlyList<ColumnMapping> columns, IReadOnlyList<int> key,
        CancellationToken cancellationToken)
    {
        var mappings = key.Select(t => columns.First(c => c.Target == t)).ToArray();
        var trim = key.Select(i => target.Columns[i]).Concat(mappings.Select(m => source.Details.Columns[m.Source])).Any(c => OracleTypes.IsFixedChar(c.DataType));
        var byText = new Dictionary<string, List<RowData>>(StringComparer.Ordinal);
        var keys = new List<IReadOnlyList<object>>();
        foreach (var row in rows)
        {
            var written = mappings.Select(m => m.ValueOf(source.Details, target, row)).ToArray();
            if (written.Any(v => v is null))
            {
                continue; // a NULL in a unique key never conflicts
            }

            object[] values = [.. written.Select(v => trim && v is string s ? s.TrimEnd(' ') : v!)];
            var text = RowTraversal.KeyText(values, trim);
            if (!byText.TryGetValue(text, out var list))
            {
                byText[text] = list = [];
                keys.Add(values);
            }

            list.Add(row);
        }

        var matched = new Dictionary<RowKey, (RowData Source, RowData Target)>();
        if (keys.Count == 0)
        {
            return matched;
        }

        foreach (var found in await targetData.ReadByKeysAsync(target, key, keys, wholeLobs: false, maxRows: null, cancellationToken))
        {
            var text = RowTraversal.KeyText([.. key.Select(i => trim && found.Values[i] is string s ? s.TrimEnd(' ') : found.Values[i]!)], trim);
            foreach (var row in byText.GetValueOrDefault(text) ?? [])
            {
                matched.TryAdd(row.Key, (row, found));
            }
        }

        return matched;
    }

    /// <summary>
    /// The rows of a table, each after the rows of the same table it references (a manager before the staff); otherwise in
    /// the order found. Rows that reference each other in a circle cannot be inserted one after the other: an error.
    /// </summary>
    private static List<RowData> InsertOrder(RowGraph graph, TableRows table, List<CopyIssue> issues)
    {
        var self = graph.RelationsLinked
            .Where(r => RelationGraph.IsSelfReference(r) && r.From == table.Table)
            .SelectMany(graph.LinksAlong)
            .Where(l => !l.Child.Equals(l.Parent))
            .Select(l => (l.Child, l.Parent))
            .Distinct()
            .ToList();
        if (self.Count == 0)
        {
            return [.. table.Rows];
        }

        // Level by level, each level in the order found: a row is ready once the last of its parents is placed.
        var position = table.Rows.Select((row, i) => (row.Key, i)).ToDictionary(p => p.Key, p => p.i);
        var waiting = table.Rows.ToDictionary(r => r.Key, _ => 0);
        foreach (var (child, _) in self)
        {
            waiting[child]++;
        }

        var staff = self.ToLookup(l => l.Parent, l => l.Child);
        var ordered = new List<RowData>(table.Rows.Count);
        var level = table.Rows.Where(r => waiting[r.Key] == 0).ToList();
        while (level.Count > 0)
        {
            ordered.AddRange(level);
            level = [.. level.SelectMany(r => staff[r.Key]).Where(child => --waiting[child] == 0)
                .Select(child => position[child]).Order().Select(i => table.Rows[i])];
        }

        if (ordered.Count < table.Rows.Count)
        {
            issues.Add(new CopyIssue(CopySeverity.Error, table.Table, TextFormat.Format(RelationText.CopyRowCycle, table.Table.Name)));
            ordered.AddRange(table.Rows.Where(r => waiting[r.Key] > 0));
        }

        return ordered;
    }
}
