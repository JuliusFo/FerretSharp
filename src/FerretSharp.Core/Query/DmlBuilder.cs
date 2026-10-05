using System.Globalization;
using System.Text;
using FerretSharp.Core.Data;
using FerretSharp.Core.Oracle;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Query;

/// <summary>
/// Builds the statements that write pending changes (v2, WP-09, CLAUDE.md 5.6): lock the row with
/// <c>SELECT … FOR UPDATE WAIT n</c> (returns its current values for the concurrency check), then UPDATE/DELETE by
/// row key or INSERT … RETURNING ROWID. Identifiers quoted, values always bound; nothing here runs anything.
/// </summary>
public static class DmlBuilder
{
    public const int DefaultLockWaitSeconds = 3;
    public const int MaxLockWaitSeconds = 60;
    public const string RowIdOutput = "p_rowid";

    /// <summary>
    /// Locks the row (waits at most <paramref name="waitSeconds"/>, then ORA-30006) and returns the columns in the
    /// given order – or its ROWID if none are asked for. No row means it was deleted meanwhile.
    /// </summary>
    public static QuerySpec Lock(TableDetails table, RowKey key, IReadOnlyList<int> columns, int waitSeconds)
    {
        if (waitSeconds is < 0 or > MaxLockWaitSeconds)
        {
            throw new ArgumentOutOfRangeException(nameof(waitSeconds), waitSeconds, $"0 bis {MaxLockWaitSeconds} Sekunden.");
        }

        var parameters = new List<QueryParameter>();
        var select = columns.Count == 0 ? "ROWID" : string.Join(", ", columns.Select(c => OracleIdentifier.Quote(table.Columns[c].Name)));
        var sql = $"SELECT {select}\n  FROM {Target(table)}\n WHERE {KeyCondition(table, key, parameters)}\n   FOR UPDATE WAIT "
                  + waitSeconds.ToString(CultureInfo.InvariantCulture);
        return new QuerySpec(sql, parameters);
    }

    public static QuerySpec Update(TableDetails table, RowKey key, IReadOnlyDictionary<int, object?> values)
    {
        if (values.Count == 0)
        {
            throw new ArgumentException("Nichts zu ändern.", nameof(values));
        }

        var parameters = new List<QueryParameter>();
        var assignments = values.OrderBy(v => v.Key).Select(v => $"{OracleIdentifier.Quote(table.Columns[v.Key].Name)} = {Bind(table, v.Key, v.Value, parameters)}");
        var sql = new StringBuilder()
            .Append("UPDATE ").Append(Target(table))
            .Append("\n   SET ").AppendJoin(",\n       ", assignments)
            .Append("\n WHERE ").Append(KeyCondition(table, key, parameters))
            .ToString();
        return new QuerySpec(sql, parameters);
    }

    public static QuerySpec Delete(TableDetails table, RowKey key)
    {
        var parameters = new List<QueryParameter>();
        return new QuerySpec($"DELETE FROM {Target(table)}\n WHERE {KeyCondition(table, key, parameters)}", parameters);
    }

    /// <summary>
    /// Only the given (filled) columns, so defaults, identity values and triggers apply to the others. Returns the
    /// new ROWID in the output parameter <see cref="RowIdOutput"/>.
    /// </summary>
    public static QuerySpec Insert(TableDetails table, IReadOnlyDictionary<int, object?> values)
    {
        var parameters = new List<QueryParameter>();
        string columns, binds;
        if (values.Count == 0)
        {
            // Oracle has no DEFAULT VALUES clause: one column with DEFAULT gives every column its default.
            columns = OracleIdentifier.Quote(table.Columns[0].Name);
            binds = "DEFAULT";
        }
        else
        {
            var ordered = values.OrderBy(v => v.Key).ToList();
            columns = string.Join(", ", ordered.Select(v => OracleIdentifier.Quote(table.Columns[v.Key].Name)));
            binds = string.Join(", ", ordered.Select(v => Bind(table, v.Key, v.Value, parameters)));
        }

        parameters.Add(new QueryParameter(RowIdOutput, null, OracleTypeHint.RowId, Output: true));
        return new QuerySpec($"INSERT INTO {Target(table)} ({columns})\nVALUES ({binds})\nRETURNING ROWID INTO :{RowIdOutput}", parameters);
    }

    /// <summary>The statements a flush would run, in order (for "Änderungen als SQL anzeigen").</summary>
    public static IReadOnlyList<QuerySpec> Describe(TableDetails table, IReadOnlyList<PendingOperation> operations, int waitSeconds)
    {
        var statements = new List<QuerySpec>();
        foreach (var operation in operations)
        {
            switch (operation.Kind)
            {
                case OperationKind.Insert:
                    statements.Add(Insert(table, operation.Values));
                    break;
                case OperationKind.Update:
                    statements.Add(Lock(table, operation.Key, operation.Values.Keys.Order().ToList(), waitSeconds));
                    statements.Add(Update(table, operation.Key, operation.Values));
                    break;
                case OperationKind.Delete:
                    statements.Add(Lock(table, operation.Key, [], waitSeconds));
                    statements.Add(Delete(table, operation.Key));
                    break;
            }
        }

        return statements;
    }

    private static string Target(TableDetails table) => OracleIdentifier.Qualify(table.Table.Owner, table.Table.Name);

    private static string Bind(TableDetails table, int column, object? value, List<QueryParameter> parameters)
    {
        var name = "v" + column.ToString(CultureInfo.InvariantCulture);
        parameters.Add(new QueryParameter(name, value, OracleTypeMapper.BindType(table.Columns[column])));
        return ":" + name;
    }

    /// <summary>PK columns (in key order) or ROWID; a row without key cannot be written.</summary>
    internal static string KeyCondition(TableDetails table, RowKey key, List<QueryParameter> parameters)
    {
        switch (key)
        {
            case RowKey.PrimaryKey pk when pk.Values.Count == table.PrimaryKey.Count:
                var conditions = new List<string>();
                for (var i = 0; i < pk.Values.Count; i++)
                {
                    var column = table.Columns.First(c => c.Name == table.PrimaryKey[i]);
                    if (pk.Values[i] is null)
                    {
                        throw new InvalidOperationException($"Primärschlüssel {column.Name} ist NULL.");
                    }

                    var name = "k" + i.ToString(CultureInfo.InvariantCulture);
                    parameters.Add(new QueryParameter(name, pk.Values[i], OracleTypeMapper.BindType(column)));
                    conditions.Add($"{OracleIdentifier.Quote(column.Name)} = :{name}");
                }

                return string.Join(" AND ", conditions);
            case RowKey.RowId rowId:
                parameters.Add(new QueryParameter("k_rowid", rowId.Value, OracleTypeHint.Varchar2));
                return "ROWID = :k_rowid";
            default:
                throw new InvalidOperationException("Die Zeile hat keinen Schlüssel und lässt sich nicht schreiben.");
        }
    }
}
