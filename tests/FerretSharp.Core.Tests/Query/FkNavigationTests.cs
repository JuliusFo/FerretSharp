using FerretSharp.Core.Data;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;
using NSubstitute;

namespace FerretSharp.Core.Tests.Query;

public class FkNavigationTests
{
    private static ColumnInfo Col(string name, string type, int? precision = null, int? scale = null, int? length = null) =>
        new(name, type, length, false, precision, scale, true, false, null, 0);

    private static readonly TableRef KundenRef = new("APP", "KUNDEN");
    private static readonly TableRef AuftragRef = new("APP", "AUFTRAG");
    private static readonly TableRef PositionRef = new("APP", "POSITION");

    private static readonly TableDetails Auftrag = new(
        new TableSummary("APP", "AUFTRAG", TableKind.Table),
        [Col("MANDANT", "NUMBER", 4, 0), Col("AUFTRAG_NR", "NUMBER", 10, 0), Col("KUNDE_ID", "NUMBER", 10, 0), Col("NOTIZ", "CLOB")],
        ["MANDANT", "AUFTRAG_NR"], [], false);

    private static readonly ForeignKeyInfo AuftragKunde = new("FK_AUFTRAG_KUNDE", AuftragRef, ["KUNDE_ID"], KundenRef, ["ID"], FkSource.Declared);

    private static readonly ForeignKeyInfo PositionAuftrag =
        new("FK_POS_AUFTRAG", PositionRef, ["POS_MANDANT", "POS_AUFTRAG"], AuftragRef, ["MANDANT", "AUFTRAG_NR"], FkSource.Declared);

    private static RowData Row(params object?[] values) => new(RowKey.None.Instance, values);

    [Fact]
    public void Outgoing_jump_filters_the_referenced_key_by_the_rows_fk_values()
    {
        var jump = FkNavigation.Outgoing(Auftrag, Row(1m, 4711m, 815m, null), AuftragKunde);

        Assert.True(jump.IsAvailable);
        Assert.Equal(JumpDirection.Outgoing, jump.Direction);
        Assert.Equal(KundenRef, jump.Table);
        var filter = Assert.Single(jump.Filters);
        Assert.Equal(("ID", FilterOperator.Equals, "815"), (filter.Column, filter.Op, filter.Values.Single()));
        Assert.Equal("ID = 815", jump.Condition);
    }

    [Fact]
    public void Incoming_jump_over_a_composite_key_filters_every_fk_column()
    {
        var jump = FkNavigation.Incoming(Auftrag, Row(1m, 4711m, 815m, null), PositionAuftrag);

        Assert.Equal(PositionRef, jump.Table);
        Assert.Equal(
            [("POS_MANDANT", "1"), ("POS_AUFTRAG", "4711")],
            jump.Filters.Select(f => (f.Column, f.Values.Single())));
        Assert.Equal("POS_MANDANT = 1, POS_AUFTRAG = 4711", jump.Condition);
    }

    [Fact]
    public void Null_key_makes_the_jump_unavailable()
    {
        var jump = FkNavigation.Outgoing(Auftrag, Row(1m, 4711m, null, null), AuftragKunde);

        Assert.False(jump.IsAvailable);
        Assert.Equal("KUNDE_ID ist NULL.", jump.Unavailable);
        Assert.Empty(jump.Filters);
    }

    [Fact]
    public void Jumps_list_outgoing_before_incoming_sorted_by_table()
    {
        var other = new ForeignKeyInfo("FK_AUFTRAG_AAA", AuftragRef, ["MANDANT"], new TableRef("APP", "AAA"), ["ID"], FkSource.Declared);

        var jumps = FkNavigation.JumpsFor(Auftrag, Row(1m, 4711m, 815m, null), [AuftragKunde, other], [PositionAuftrag]);

        Assert.Equal(["AAA", "KUNDEN", "POSITION"], jumps.Select(j => j.Table.Name));
        Assert.Equal([JumpDirection.Outgoing, JumpDirection.Outgoing, JumpDirection.Incoming], jumps.Select(j => j.Direction));
    }

    public static TheoryData<string, object> RoundTripValues() => new()
    {
        { "NUMBER", 4711m },
        { "NUMBER", 1234567.89m },
        { "NUMBER", -0.5m },
        { "NUMBER", 1234567890123456789012345678m },
        { "DATE", new DateTime(2026, 10, 1, 13, 45, 7) },
        { "DATE", new DateTime(2026, 10, 1) },
        { "TIMESTAMP(6)", new DateTime(2026, 10, 1, 13, 45, 7).AddTicks(1234560) },
        { "VARCHAR2", "1.234" },
        { "VARCHAR2", "Müller; Köln" },
        { "CHAR", "AB " },
        { "RAW", new byte[] { 0xCA, 0xFE, 0x00, 0x01 } },
    };

    /// <summary>The filter text must validate and bind exactly the original value – also where the grid text would not.</summary>
    [Theory]
    [MemberData(nameof(RoundTripValues))]
    public void Filter_values_parse_back_to_the_original_value(string type, object value)
    {
        var column = Col("C", type, scale: type.StartsWith("TIMESTAMP", StringComparison.Ordinal) ? 6 : null, length: type is "VARCHAR2" or "CHAR" or "RAW" ? 20 : null);
        var table = new TableDetails(new TableSummary("APP", "T", TableKind.Table), [column], ["C"], [], false);

        var text = FkNavigation.FilterValue(column, value);
        var filter = FilterCondition.Of("C", FilterOperator.Equals, text!);
        var query = QueryBuilder.BuildCount(table, [filter]);

        Assert.NotNull(text);
        Assert.Null(FilterRules.Validate(column, filter));
        Assert.Contains("\"C\" = :p0", query.Sql);
        Assert.Equal(value, query.Parameters.Single().Value);
    }

    [Fact]
    public void Number_that_the_grid_shows_grouped_is_written_invariant()
    {
        var column = Col("ID", "NUMBER", 10, 0);

        Assert.Equal("1.234", CellFormatter.Format(column, 1234m));
        Assert.Equal("1234", FkNavigation.FilterValue(column, 1234m));
    }

    [Theory]
    [InlineData("CLOB")]
    [InlineData("BINARY_DOUBLE")]
    [InlineData("NUMBER")]
    public void Types_without_exact_equality_are_not_navigable(string type)
    {
        object value = type switch
        {
            "CLOB" => new LobValue("x", 1),
            "BINARY_DOUBLE" => 1.5d,
            _ => new BigNumber("12345678901234567890123456789012345678"),
        };

        Assert.Null(FkNavigation.FilterValue(Col("C", type), value));
    }

    [Fact]
    public async Task Count_returns_the_number_of_referencing_rows()
    {
        var data = Substitute.For<IDataAccess>();
        var jump = FkNavigation.Incoming(Auftrag, Row(1m, 4711m, 815m, null), PositionAuftrag);
        data.CountAsync(Auftrag, jump.Filters, Arg.Any<CancellationToken>()).Returns(12L);

        Assert.Equal(12L, await FkNavigation.CountAsync(data, Auftrag, jump, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Count_gives_up_after_the_timeout_and_cancels_the_statement()
    {
        var data = Substitute.For<IDataAccess>();
        CancellationToken seen = default;
        data.CountAsync(Arg.Any<TableDetails>(), Arg.Any<IReadOnlyList<FilterCondition>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                seen = call.ArgAt<CancellationToken>(2);
                await Task.Delay(Timeout.Infinite, seen);
                return 0L;
            });
        var jump = FkNavigation.Outgoing(Auftrag, Row(1m, 4711m, 815m, null), AuftragKunde);

        var count = await FkNavigation.CountAsync(data, Auftrag, jump, TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);

        Assert.Null(count);
        Assert.True(seen.IsCancellationRequested);
    }

    [Fact]
    public async Task Cancelling_the_count_is_not_reported_as_timeout()
    {
        var data = Substitute.For<IDataAccess>();
        data.CountAsync(Arg.Any<TableDetails>(), Arg.Any<IReadOnlyList<FilterCondition>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                await Task.Delay(Timeout.Infinite, call.ArgAt<CancellationToken>(2));
                return 0L;
            });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var jump = FkNavigation.Outgoing(Auftrag, Row(1m, 4711m, 815m, null), AuftragKunde);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => FkNavigation.CountAsync(data, Auftrag, jump, TimeSpan.FromSeconds(30), cts.Token));
    }
}
