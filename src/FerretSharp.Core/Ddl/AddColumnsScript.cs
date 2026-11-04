using FerretSharp.Core.Resources;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Ddl;

/// <summary>
/// The script that adds columns of a table to the same table on another database (the columns view's menu): one
/// <c>ALTER TABLE … ADD</c> per column in schema order, the foreign keys made only of these columns, then their comments –
/// the steps of <see cref="ColumnChange"/>, so the warnings (NOT NULL without default) are those of the schema comparison.
/// Shown and opened in a SQL tab, never run from here.
/// </summary>
public static class AddColumnsScript
{
    /// <param name="source">The table the columns come from, named in the header.</param>
    /// <param name="target">The table the statements change: on another connection the matching table there (the owner may differ).</param>
    /// <param name="columns">The columns to add; their order does not matter (schema order, so a virtual column follows its base columns).</param>
    /// <param name="constraints">
    /// The source table's constraints: its foreign keys whose columns are all added come along. Null when they could not be
    /// read – the header then says the foreign keys are missing.
    /// </param>
    public static string Write(TableRef source, TableRef target, IEnumerable<ColumnInfo> columns, IReadOnlyList<ConstraintInfo>? constraints)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(columns);
        var added = columns.OrderBy(c => c.Position).ToList();
        var steps = added.SelectMany(c => ColumnChange.Steps(target, null, c, ChangeIntent.Known)).ToList();
        steps.AddRange(ForeignKeys(source, target, added, constraints ?? []));
        List<string> header = ["-- " + TextFormat.Format(CompareText.AddColumnsHeader, source)];
        if (constraints is null)
        {
            header.Add("-- " + CompareText.AddColumnsWithoutForeignKeys);
        }

        return DdlScript.Render(DdlScript.Order(steps), header);
    }

    /// <summary>
    /// The foreign keys of <paramref name="source"/> made only of added columns. A referenced table of the source's owner is
    /// the one of the target's owner (as in the schema comparison), the source table itself the target table. One that
    /// checks rows – every column gets a value at once (a default, identity, virtual): Oracle checks no row with a NULL in
    /// the key – carries the warning of the comparison. One that references added columns of its own table warns that the
    /// key it needs is not in the script (review, finding 8). One whose parent the dictionary does not show (no privileges
    /// on its owner) is named as a note instead of dropped silently.
    /// </summary>
    private static IEnumerable<DdlStep> ForeignKeys(TableRef source, TableRef target, IReadOnlyList<ColumnInfo> added, IReadOnlyList<ConstraintInfo> constraints)
    {
        var byName = added.ToDictionary(c => c.Name, StringComparer.Ordinal);
        foreach (var fk in constraints.Where(c => c.Type == ConstraintType.ForeignKey && c.Columns.Count > 0))
        {
            if (!fk.Columns.All(byName.ContainsKey))
            {
                continue;
            }

            if (fk.References is null || fk.ReferencedColumns.Count != fk.Columns.Count)
            {
                yield return DdlStep.Hint(target.Name, TextFormat.Format(CompareText.AddColumnsForeignKeyUnreadable, fk.Name), null);
                continue;
            }

            var checksRows = fk.Enabled && fk.Validated && fk.Columns.All(c => GetsValue(byName[c]));
            var ownKey = fk.References == source && fk.ReferencedColumns.Any(byName.ContainsKey);
            List<string> warnings = [];
            if (checksRows)
            {
                warnings.Add(CompareText.ChecksExistingRows);
            }

            if (ownKey)
            {
                warnings.Add(TextFormat.Format(CompareText.AddColumnsOwnKeyNeeded, string.Join(", ", fk.ReferencedColumns)));
            }

            yield return new DdlStep(target.Name, DdlStepKind.AddForeignKey, OracleDdl.AddConstraint(target, OracleDdl.ConstraintClause(fk, Map)),
                checksRows ? DdlRisk.MayFailOnValues : DdlRisk.Safe, warnings.Count > 0 ? string.Join("\n", warnings) : null);
        }

        static bool GetsValue(ColumnInfo column) => column.Default is not null || column.IsIdentity || column.IsVirtual;

        TableRef Map(TableRef table) =>
            table == source ? target
            : string.Equals(table.Owner, source.Owner, StringComparison.Ordinal) ? new TableRef(target.Owner, table.Name)
            : table;
    }
}
