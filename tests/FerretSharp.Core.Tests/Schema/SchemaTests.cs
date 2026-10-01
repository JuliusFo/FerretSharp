using FerretSharp.Core.Schema;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace FerretSharp.Core.Tests.Schema;

public class ColumnInfoTests
{
    private static ColumnInfo Column(string type, int? length = null, bool charSemantics = false, int? precision = null, int? scale = null) =>
        new("C", type, length, charSemantics, precision, scale, true, false, null, 1);

    [Theory]
    [InlineData("VARCHAR2", 50, true, null, null, "VARCHAR2(50 CHAR)")]
    [InlineData("VARCHAR2", 50, false, null, null, "VARCHAR2(50)")]
    [InlineData("NVARCHAR2", 20, true, null, null, "NVARCHAR2(20)")]
    [InlineData("RAW", 16, false, null, null, "RAW(16)")]
    [InlineData("NUMBER", null, false, 10, 0, "NUMBER(10)")]
    [InlineData("NUMBER", null, false, 12, 2, "NUMBER(12,2)")]
    [InlineData("NUMBER", null, false, null, 0, "INTEGER")]
    [InlineData("NUMBER", null, false, null, null, "NUMBER")]
    [InlineData("TIMESTAMP(6)", null, false, null, 6, "TIMESTAMP(6)")]
    [InlineData("DATE", null, false, null, null, "DATE")]
    public void Display_type_matches_ddl_notation(string type, int? length, bool charSemantics, int? precision, int? scale, string expected)
    {
        Assert.Equal(expected, Column(type, length, charSemantics, precision, scale).DisplayType);
    }
}

public class SchemaCacheTests
{
    private const string Owner = "APP";
    private static readonly TableSummary Kunden = new(Owner, "KUNDEN", TableKind.Table);
    private static readonly TableSummary Auftrag = new(Owner, "AUFTRAG", TableKind.Table);
    private static readonly ForeignKeyInfo AuftragKunde = new(
        "FK_AUFTRAG_KUNDE", Auftrag.Ref, ["KUNDE_ID"], Kunden.Ref, ["KUNDE_ID"], FkSource.Declared);

    private readonly ISchemaReader _reader = Substitute.For<ISchemaReader>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public SchemaCacheTests()
    {
        _reader.GetTablesAsync(Owner, Arg.Any<CancellationToken>()).Returns([Auftrag, Kunden]);
        _reader.GetForeignKeysAsync(Owner, Arg.Any<CancellationToken>()).Returns([AuftragKunde]);
        _reader.GetDetailsAsync(Arg.Any<TableSummary>(), Arg.Any<CancellationToken>())
            .Returns(ci => new TableDetails(ci.Arg<TableSummary>(), [], [], [], false));
    }

    [Fact]
    public async Task Load_exposes_tables_and_relationship_lookups()
    {
        var cache = new SchemaCache(_reader, Owner);

        await cache.LoadAsync(Ct);

        Assert.Equal([Auftrag, Kunden], cache.Tables);
        Assert.Equal([AuftragKunde], cache.IncomingOf(Kunden.Ref));
        Assert.Equal([AuftragKunde], cache.OutgoingOf(Auftrag.Ref));
        Assert.Empty(cache.OutgoingOf(Kunden.Ref));
        Assert.Same(Kunden, cache.Find(Kunden.Ref));
        Assert.NotNull(cache.LoadedAt);
    }

    [Fact]
    public async Task Details_are_loaded_once_per_table()
    {
        var cache = new SchemaCache(_reader, Owner);
        await cache.LoadAsync(Ct);

        await cache.GetDetailsAsync(Kunden, Ct);
        await cache.GetDetailsAsync(Kunden, Ct);
        await cache.GetDetailsAsync(Auftrag, Ct);

        await _reader.Received(1).GetDetailsAsync(Kunden, Arg.Any<CancellationToken>());
        await _reader.Received(1).GetDetailsAsync(Auftrag, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Failed_detail_loads_are_retried()
    {
        _reader.GetDetailsAsync(Kunden, Arg.Any<CancellationToken>())
            .Returns(
                _ => Task.FromException<TableDetails>(new InvalidOperationException("boom")),
                ci => Task.FromResult(new TableDetails(Kunden, [], [], [], false)));
        var cache = new SchemaCache(_reader, Owner);
        await cache.LoadAsync(Ct);

        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetDetailsAsync(Kunden, Ct));
        var details = await cache.GetDetailsAsync(Kunden, Ct);

        Assert.Same(Kunden, details.Table);
    }

    [Fact]
    public async Task Refresh_reloads_and_drops_cached_details()
    {
        var cache = new SchemaCache(_reader, Owner);
        await cache.LoadAsync(Ct);
        await cache.GetDetailsAsync(Kunden, Ct);

        await cache.RefreshAsync(Ct);
        await cache.GetDetailsAsync(Kunden, Ct);

        await _reader.Received(2).GetTablesAsync(Owner, Arg.Any<CancellationToken>());
        await _reader.Received(2).GetDetailsAsync(Kunden, Arg.Any<CancellationToken>());
    }
}
