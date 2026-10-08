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

    private static readonly TableDetails Kunden = new(
        new TableSummary("APP", "KUNDEN", TableKind.Table), [Col("ID", "NUMBER", 10, 0), Col("NAME", "VARCHAR2", length: 50)], ["ID"], [], false);

    /// <summary>AUFTRAG rows (MANDANT, AUFTRAG_NR, KUNDE_ID, NOTIZ) with the given customer ids.</summary>
    private static RowData[] Orders(params decimal?[] kundeIds) =>
        kundeIds.Select((id, i) => Row(1m, 4711m + i, id, null)).ToArray();

    [Fact]
    public void Several_rows_jump_with_an_in_filter_over_their_distinct_values_in_selection_order()
    {
        var jump = FkNavigation.Outgoing(Auftrag, Orders(815m, 4711m, 815m, 12m), AuftragKunde);

        Assert.True(jump.IsAvailable);
        var filter = Assert.Single(jump.Filters);
        Assert.Equal(("ID", FilterOperator.In), (filter.Column, filter.Op));
        Assert.Equal(["815", "4711", "12"], filter.Values);
        Assert.Equal(4, jump.Rows);
        Assert.Equal("ID in (815; 4711; 12) · 3 Werte", jump.Condition);
        Assert.Null(jump.SkippedNote);
    }

    [Fact]
    public void Condition_of_a_long_value_list_shows_the_first_values_and_the_count()
    {
        var jump = FkNavigation.Outgoing(Auftrag, Orders(1m, 2m, 3m, 4m, 5m), AuftragKunde);

        Assert.Equal("ID in (1; 2; 3; …) · 5 Werte", jump.Condition);
    }

    [Fact]
    public void Several_rows_with_the_same_value_jump_with_an_equality_filter()
    {
        var jump = FkNavigation.Outgoing(Auftrag, Orders(815m, 815m), AuftragKunde);

        var filter = Assert.Single(jump.Filters);
        Assert.Equal(("ID", FilterOperator.Equals, "815"), (filter.Column, filter.Op, filter.Values.Single()));
        Assert.Equal("ID = 815", jump.Condition);
    }

    [Fact]
    public void Rows_without_a_value_are_skipped_and_reported()
    {
        var jump = FkNavigation.Outgoing(Auftrag, Orders(815m, null, 4711m, null), AuftragKunde);

        Assert.True(jump.IsAvailable);
        Assert.Equal(["815", "4711"], Assert.Single(jump.Filters).Values);
        Assert.Equal(2, jump.SkippedRows);
        Assert.Equal("2 Zeilen ohne Wert übersprungen", jump.SkippedNote);
    }

    [Fact]
    public void One_skipped_row_is_reported_in_singular()
    {
        var jump = FkNavigation.Outgoing(Auftrag, Orders(815m, null), AuftragKunde);

        Assert.Equal("ID = 815", jump.Condition);
        Assert.Equal("1 Zeile ohne Wert übersprungen", jump.SkippedNote);
    }

    [Fact]
    public void Jump_is_unavailable_if_no_selected_row_has_a_value()
    {
        var jump = FkNavigation.Outgoing(Auftrag, Orders(null, null, null), AuftragKunde);

        Assert.False(jump.IsAvailable);
        Assert.Equal("KUNDE_ID ist in allen 3 Zeilen NULL.", jump.Unavailable);
        Assert.Empty(jump.Filters);
    }

    [Fact]
    public void Several_rows_over_a_type_without_exact_equality_are_not_navigable()
    {
        var messwert = new TableDetails(
            new TableSummary("APP", "MESSWERT", TableKind.Table), [Col("ID", "NUMBER", 10, 0), Col("FAKTOR", "BINARY_DOUBLE")], ["ID"], [], false);
        var fk = new ForeignKeyInfo("FK_MESSWERT_FAKTOR", messwert.Table.Ref, ["FAKTOR"], new TableRef("APP", "FAKTOR"), ["WERT"], FkSource.Declared);

        var jump = FkNavigation.Outgoing(messwert, [Row(1m, 1.5d), Row(2m, 2.5d)], fk);

        Assert.False(jump.IsAvailable);
        Assert.Equal("Sprung über BINARY_DOUBLE nicht möglich.", jump.Unavailable);
    }

    [Fact]
    public void Composite_key_that_is_the_same_in_all_rows_jumps_with_equality_filters()
    {
        var jump = FkNavigation.Incoming(Auftrag, [Row(1m, 4711m, 815m, null), Row(1m, 4711m, 999m, null)], PositionAuftrag);

        Assert.True(jump.IsAvailable);
        Assert.Equal(
            [("POS_MANDANT", FilterOperator.Equals, "1"), ("POS_AUFTRAG", FilterOperator.Equals, "4711")],
            jump.Filters.Select(f => (f.Column, f.Op, f.Values.Single())));
    }

    [Fact]
    public void Composite_key_that_differs_between_rows_is_not_navigable()
    {
        var jump = FkNavigation.Incoming(Auftrag, [Row(1m, 4711m, 815m, null), Row(1m, 4712m, 815m, null)], PositionAuftrag);

        Assert.False(jump.IsAvailable);
        Assert.Equal("Bei mehreren Zeilen nur für Schlüssel aus einer Spalte möglich.", jump.Unavailable);
    }

    [Fact]
    public void Composite_key_skips_rows_where_one_column_is_null()
    {
        var jump = FkNavigation.Incoming(Auftrag, [Row(1m, 4711m, 815m, null), Row(null, 4712m, 815m, null)], PositionAuftrag);

        Assert.Equal("POS_MANDANT = 1, POS_AUFTRAG = 4711", jump.Condition);
        Assert.Equal(1, jump.SkippedRows);
    }

    [Fact]
    public void At_most_1000_distinct_values()
    {
        var thousand = FkNavigation.Outgoing(Auftrag, Orders([.. Enumerable.Range(1, 1000).Select(i => (decimal?)i)]), AuftragKunde);
        var more = FkNavigation.Outgoing(Auftrag, Orders([.. Enumerable.Range(1, 1001).Select(i => (decimal?)i)]), AuftragKunde);

        Assert.Equal(1000, Assert.Single(thousand.Filters).Values.Count);
        Assert.EndsWith("· 1.000 Werte", thousand.Condition);
        Assert.False(more.IsAvailable);
        Assert.Equal("1.001 verschiedene Werte – höchstens 1.000 möglich.", more.Unavailable);
    }

    [Fact]
    public void Duplicates_do_not_count_towards_the_limit()
    {
        var jump = FkNavigation.Outgoing(Auftrag, Orders([.. Enumerable.Range(1, 1500).Select(i => (decimal?)(i % 10))]), AuftragKunde);

        Assert.Equal(10, Assert.Single(jump.Filters).Values.Count);
    }

    [Fact]
    public void Text_value_containing_the_list_separator_cannot_be_part_of_a_list()
    {
        var table = new TableDetails(
            new TableSummary("APP", "T", TableKind.Table), [Col("ID", "NUMBER", 10, 0), Col("CODE", "VARCHAR2", length: 20)], ["ID"], [], false);
        var fk = new ForeignKeyInfo("FK_T_CODE", table.Table.Ref, ["CODE"], new TableRef("APP", "CODES"), ["CODE"], FkSource.Declared);

        var list = FkNavigation.Outgoing(table, [Row(1m, "A;B"), Row(2m, "C")], fk);
        var same = FkNavigation.Outgoing(table, [Row(1m, "A;B"), Row(2m, "A;B")], fk);

        Assert.False(list.IsAvailable);
        Assert.Equal("Ein Wert enthält „;“ – bei mehreren Werten nicht möglich.", list.Unavailable);
        Assert.Equal("CODE = A;B", same.Condition);
    }

    [Fact]
    public void Incoming_jump_from_several_rows_over_a_relation_of_the_csharp_model()
    {
        var bearbeiter = new ForeignKeyInfo("Auftrag.Kunde", AuftragRef, ["KUNDE_ID"], KundenRef, ["ID"], FkSource.ClrModel);

        var jumps = FkNavigation.JumpsFor(Kunden, [Row(815m, "A"), Row(4711m, "B")], [], [bearbeiter]);

        var jump = Assert.Single(jumps);
        Assert.Equal((JumpDirection.Incoming, AuftragRef, FkSource.ClrModel), (jump.Direction, jump.Table, jump.ForeignKey.Source));
        Assert.Equal("KUNDE_ID in (815; 4711) · 2 Werte", jump.Condition);
    }

    /// <summary>The list must validate and bind each value like the single-row filter does.</summary>
    [Fact]
    public void In_filter_of_a_jump_validates_and_binds_every_value()
    {
        var jump = FkNavigation.Incoming(Kunden, [Row(815m, "A"), Row(4711m, "B"), Row(12m, "C")], AuftragKunde);
        var filter = Assert.Single(jump.Filters);

        var query = QueryBuilder.BuildCount(Auftrag, jump.Filters);

        Assert.Null(FilterRules.Validate(Auftrag.Columns[2], filter));
        Assert.Contains(" IN (", query.Sql);
        Assert.Equal([815m, 4711m, 12m], query.Parameters.Select(p => p.Value));
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
