using FerretSharp.Core.Data;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;
using NSubstitute;

namespace FerretSharp.Core.Tests.Relations;

/// <summary>
/// Tables with rows in memory behind <see cref="IDataAccess.ReadByKeysAsync"/>, compared like Oracle would (numbers by
/// value, CHAR blank-padded), and a log of the reads.
/// </summary>
internal sealed class MemoryTables
{
    private readonly Dictionary<TableRef, (TableDetails Details, List<object?[]> Rows)> _tables = [];

    public MemoryTables()
    {
        Data = Substitute.For<IDataAccess>();
        Data.ReadByKeysAsync(default!, default, default!, default, default, default).ReturnsForAnyArgs(call => Task.FromResult(Read(
            call.ArgAt<TableDetails>(0), call.ArgAt<IReadOnlyList<int>?>(1), call.ArgAt<IReadOnlyList<IReadOnlyList<object>>>(2), call.ArgAt<int?>(4))));
    }

    public IDataAccess Data { get; }

    /// <summary>Each read: table and number of keys.</summary>
    public List<(TableRef Table, int Keys)> Reads { get; } = [];

    public static ColumnInfo Col(string name, string type = "NUMBER", bool nullable = true, int? length = null) =>
        new(name, type, length, false, type == "NUMBER" ? 10 : null, type == "NUMBER" ? 0 : null, nullable, false, null, 0);

    public static TableDetails Table(string name, string[] key, params ColumnInfo[] columns) =>
        new(new TableSummary("APP", name, TableKind.Table), columns, key, [], false);

    /// <summary>The same table in another schema (the target of a copy).</summary>
    public static TableDetails In(string owner, TableDetails table) => table with { Table = table.Table with { Owner = owner } };

    /// <summary>
    /// The schema of <paramref name="owner"/> over these tables, loaded; <paramref name="triggers"/> are the enabled
    /// triggers per table.
    /// </summary>
    public async Task<SchemaCache> SchemaAsync(
        string owner, IReadOnlyDictionary<TableRef, IReadOnlyList<TableRef>>? triggers = null, IReadOnlyList<ForeignKeyInfo>? foreignKeys = null)
    {
        var reader = Substitute.For<ISchemaReader>();
        reader.GetTablesAsync(owner, Arg.Any<CancellationToken>()).Returns(
            [.. _tables.Values.Select(t => t.Details.Table).Where(t => t.Owner == owner).OrderBy(t => t.Name, StringComparer.Ordinal)]);
        reader.GetSynonymTargetsAsync(owner, Arg.Any<CancellationToken>()).Returns(SynonymTargets.None);
        reader.GetPlSqlObjectsAsync(owner, Arg.Any<CancellationToken>()).Returns([]);
        reader.GetForeignKeysAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).Returns(foreignKeys ?? []);
        reader.GetPrimaryKeysAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).Returns([]);
        reader.GetDetailsAsync(Arg.Any<TableSummary>(), Arg.Any<CancellationToken>()).Returns(call => _tables[call.Arg<TableSummary>().Ref].Details);
        reader.GetTableTriggersAsync(Arg.Any<TableRef>(), Arg.Any<CancellationToken>()).Returns(call =>
            triggers?.GetValueOrDefault(call.Arg<TableRef>()) ?? (IReadOnlyList<TableRef>)[]);
        var schema = new SchemaCache(reader, owner);
        await schema.LoadAsync(TestContext.Current.CancellationToken);
        return schema;
    }

    public TableDetails Add(TableDetails table, params object?[][] rows)
    {
        _tables[table.Table.Ref] = (table, [.. rows]);
        return table;
    }

    public Task<TableDetails?> DetailsOf(TableRef table, CancellationToken cancellationToken) =>
        Task.FromResult(_tables.TryGetValue(table, out var t) ? t.Details : null);

    public static RowKey KeyOf(TableDetails table, object?[] values) =>
        table.PrimaryKey.Count > 0
            ? new RowKey.PrimaryKey([.. table.PrimaryKey.Select(c => values[table.IndexOf(c)])])
            : new RowKey.RowId("ROWID-" + string.Join("-", values));

    private IReadOnlyList<RowData> Read(TableDetails table, IReadOnlyList<int>? columns, IReadOnlyList<IReadOnlyList<object>> keys, int? maxRows)
    {
        Assert.InRange(keys.Count, 1, QueryBuilder.MaxLookupKeys * 10);
        Reads.Add((table.Table.Ref, keys.Count));
        var (details, rows) = _tables[table.Table.Ref];
        return [.. rows
            .Where(row => keys.Any(key => columns is null
                ? (string)key[0] == ((RowKey.RowId)KeyOf(details, row)).Value
                : columns.Select((c, i) => Same(details.Columns[c], row[c], key[i])).All(same => same)))
            .Select(row => new RowData(KeyOf(details, row), row))
            .Take(maxRows ?? int.MaxValue)];
    }

    private static bool Same(ColumnInfo column, object? value, object key) => (value, key) switch
    {
        (null, _) => false,
        (string s, string k) when column.DataType == "CHAR" => s.TrimEnd(' ') == k.TrimEnd(' '),
        (decimal d, decimal k) => d == k,
        _ => Equals(value, key),
    };
}
