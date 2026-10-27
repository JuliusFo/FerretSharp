using FerretSharp.Core.Data;
using FerretSharp.Core.Display;
using FerretSharp.Core.Schema;
using NSubstitute;

namespace FerretSharp.Core.Tests.Display;

/// <summary>WP-31: the names of one grid – each key asked for once, found or not found, a failing link switched off.</summary>
public sealed class DisplayNamesTests
{
    private static ColumnInfo Col(string name, string type) => new(name, type, null, false, null, null, true, false, null, 1);

    private static readonly TableDetails Kunde = new(new TableSummary("APP", "KUNDE", TableKind.Table),
        [Col("ID", "NUMBER"), Col("NAME", "VARCHAR2"), Col("KUERZEL", "CHAR")], ["ID"], [["KUERZEL"]], false);

    private static readonly TableDetails Land = new(new TableSummary("APP", "SDLAND", TableKind.Table),
        [Col("CODE", "RAW"), Col("BEZEICHNUNG", "VARCHAR2")], ["CODE"], [], false);

    private static ForeignKeyInfo Fk(string column, TableDetails to, string key) => new("FK_" + column, new("APP", "AUFTRAG"), [column], to.Table.Ref, [key], FkSource.Declared);

    // AUFTRAG: 0 ID, 1 KUNDE_ID, 2 LAND_CODE, 3 KUNDE_KZ (VARCHAR2 → CHAR)
    private static readonly DisplayLink KundeLink = new(1, Fk("KUNDE_ID", Kunde, "ID"), Kunde, 0, [new DisplayPart(1)]);
    private static readonly DisplayLink LandLink = new(2, Fk("LAND_CODE", Land, "CODE"), Land, 0, [new DisplayPart(1)]);
    private static readonly DisplayLink KuerzelLink = new(3, Fk("KUNDE_KZ", Kunde, "KUERZEL"), Kunde, 2, [new DisplayPart(1)]);

    private static DisplayNames Names(params DisplayLink[] links) => new(links, (_, _, value) => value as string);

    private static object?[] Row(object? kunde, object? land = null, object? kuerzel = null) => [1m, kunde, land, kuerzel];

    [Fact]
    public void Each_key_is_asked_for_once_per_link()
    {
        var names = Names(KundeLink, LandLink);

        var lookups = names.Missing([Row(4711m, new byte[] { 0xDE }), Row(4712m, new byte[] { 0xDE }), Row(4711m), Row(null)]);

        Assert.Equal(2, lookups.Count);
        Assert.Equal([4711m, 4712m], Assert.Single(lookups, l => l.Link == KundeLink).Keys);
        Assert.Equal([0xDE], (byte[])Assert.Single(Assert.Single(lookups, l => l.Link == LandLink).Keys));
        Assert.Empty(names.Missing([Row(4711m, new byte[] { 0xDE }), Row(4712m)])); // asked for already
    }

    [Fact]
    public void Found_rows_give_names_and_the_other_keys_are_not_found()
    {
        var names = Names(KundeLink);
        var lookup = Assert.Single(names.Missing([Row(4711m), Row(4712m), Row(4713m)]));

        names.Found(lookup, [new LookupRow(4711m, ["PetersPizza"]), new LookupRow(4713m, [null])]);

        Assert.Equal(new LookedUpName("PetersPizza", false), names.NameOf(1, 4711m));
        Assert.Equal(new LookedUpName(null, true), names.NameOf(1, 4712m));
        Assert.Equal(new LookedUpName(null, false), names.NameOf(1, 4713m)); // found, the name is NULL
        Assert.Null(names.NameOf(1, 4714m)); // not looked up
        Assert.Null(names.NameOf(1, null));
        Assert.Null(names.NameOf(0, 4711m)); // no link
        Assert.Empty(names.Missing([Row(4711m), Row(4712m)]));
    }

    [Fact]
    public void Keys_compare_by_value_whatever_their_type()
    {
        var names = Names(KundeLink, LandLink);
        var lookups = names.Missing([Row(4711, new byte[] { 0xCA, 0xFE })]);

        names.Found(lookups[0], [new LookupRow(4711m, ["Peter"])]);
        names.Found(lookups[1], [new LookupRow(new byte[] { 0xCA, 0xFE }, ["Deutschland"])]);

        Assert.Equal("Peter", names.NameOf(1, 4711L)?.Text);
        Assert.Equal("Deutschland", names.NameOf(2, new byte[] { 0xCA, 0xFE })?.Text);
    }

    [Fact]
    public void Char_keys_compare_without_their_padding()
    {
        var names = Names(KuerzelLink);
        var lookup = Assert.Single(names.Missing([Row(null, null, "AB")]));

        names.Found(lookup, [new LookupRow("AB   ", ["Peter"])]); // CHAR(5) comes back padded

        Assert.Equal("Peter", names.NameOf(3, "AB")?.Text);
    }

    [Fact]
    public void A_padded_char_value_is_bound_without_padding_for_a_varchar2_key()
    {
        var target = Kunde with { Columns = [Col("ID", "NUMBER"), Col("NAME", "VARCHAR2"), Col("KUERZEL", "VARCHAR2")] };
        var names = Names(KuerzelLink with { Target = target, SourcePadded = true });

        var lookup = Assert.Single(names.Missing([Row(null, null, "AB   ")]));

        Assert.Equal("AB", Assert.Single(lookup.Keys));
    }

    [Fact]
    public void Long_names_are_shortened_to_one_line()
    {
        var names = Names(KundeLink);
        var lookup = Assert.Single(names.Missing([Row(1m)]));

        names.Found(lookup, [new LookupRow(1m, ["Zeile eins\r\nZeile zwei " + new string('x', 100)])]);

        var text = names.NameOf(1, 1m)!.Text!;
        Assert.Equal(DisplayNames.MaxTextLength, text.Length);
        Assert.StartsWith("Zeile eins Zeile zwei", text);
        Assert.EndsWith("…", text);
    }

    [Fact]
    public void Values_that_cannot_be_looked_up_are_skipped()
    {
        var names = Names(KundeLink);

        Assert.Empty(names.Missing([Row(new BigNumber("123456789012345678901234567890123")), Row(1.5d)]));
        Assert.Null(names.NameOf(1, new BigNumber("1")));
    }

    [Fact]
    public void More_keys_than_one_lookup_takes_are_split()
    {
        var names = Names(KundeLink);

        var lookups = names.Missing(Enumerable.Range(1, 501).Select(i => Row((decimal)i)));

        Assert.Equal([500, 1], lookups.Select(l => l.Keys.Count));
    }

    [Fact]
    public void A_failed_lookup_switches_the_link_off()
    {
        var names = Names(KundeLink, LandLink);
        var lookups = names.Missing([Row(4711m, new byte[] { 1 })]);

        names.Failed(lookups[0], "ORA-00942");

        Assert.False(names.Shows(1));
        Assert.True(names.Shows(2));
        Assert.Equal("ORA-00942", names.DisabledReason(1));
        Assert.Null(names.NameOf(1, 4711m));
        Assert.All(names.Missing([Row(4712m, new byte[] { 2 })]), l => Assert.Same(LandLink, l.Link));
        names.Clear();
        Assert.False(names.Shows(1)); // stays off for the grid
    }

    [Fact]
    public void Only_timeouts_in_a_row_switch_the_link_off()
    {
        var names = Names(KundeLink);
        var lookup = Assert.Single(names.Missing([Row(4711m)]));

        Assert.False(names.TimedOut(lookup, "zu langsam"));
        Assert.False(names.TimedOut(lookup, "zu langsam"));
        names.Found(lookup, [new LookupRow(4711m, ["Peter"])]); // an answer starts the count again
        Assert.False(names.TimedOut(lookup, "zu langsam"));
        Assert.False(names.TimedOut(lookup, "zu langsam"));
        Assert.True(names.Shows(1));
        Assert.Empty(names.Missing([Row(4711m)])); // still asked for: the lookup runs again

        Assert.True(names.TimedOut(lookup, "zu langsam"));
        Assert.False(names.Shows(1));
        Assert.Equal("zu langsam", names.DisabledReason(1));
    }

    [Fact]
    public void A_dropped_lookup_may_be_asked_for_again_and_clear_forgets_everything()
    {
        var names = Names(KundeLink);
        var dropped = Assert.Single(names.Missing([Row(4711m)]));

        names.Dropped(dropped);
        var again = Assert.Single(names.Missing([Row(4711m)]));
        names.Found(again, [new LookupRow(4711m, ["Peter"])]);
        names.Clear();

        Assert.Null(names.NameOf(1, 4711m));
        Assert.Single(names.Missing([Row(4711m)]));
    }

    [Fact]
    public void A_row_is_touched_by_a_lookup_of_its_value()
    {
        var names = Names(KundeLink);
        var lookup = Assert.Single(names.Missing([Row(4711m), Row(4712m)]));

        Assert.True(names.Touches(lookup, Row(4712m)));
        Assert.True(names.Touches(lookup, Row(4712)));
        Assert.False(names.Touches(lookup, Row(4713m)));
        Assert.False(names.Touches(lookup, Row(null)));
    }

    [Fact]
    public async Task A_lookup_that_takes_too_long_is_cancelled()
    {
        var data = Substitute.For<IDataAccess>();
        CancellationToken statement = default;
        data.LookupAsync(Kunde, 0, Arg.Is<IReadOnlyList<int>>(c => c.SequenceEqual(new[] { 1 })), Arg.Any<IReadOnlyList<object>>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            statement = call.Arg<CancellationToken>();
            return Task.Delay(Timeout.Infinite, statement).ContinueWith(_ => (IReadOnlyList<LookupRow>)[], TaskScheduler.Default);
        });
        var lookup = Assert.Single(Names(KundeLink).Missing([Row(4711m)]));

        var rows = await DisplayNames.LookupAsync(data, lookup, TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);

        Assert.Null(rows);
        Assert.True(statement.IsCancellationRequested);
    }

    [Fact]
    public async Task A_lookup_returns_the_rows_it_found()
    {
        var data = Substitute.For<IDataAccess>();
        data.LookupAsync(Kunde, 0, Arg.Is<IReadOnlyList<int>>(c => c.SequenceEqual(new[] { 1 })), Arg.Any<IReadOnlyList<object>>(), Arg.Any<CancellationToken>())
            .Returns([new LookupRow(4711m, ["Peter"])]);
        var lookup = Assert.Single(Names(KundeLink).Missing([Row(4711m)]));

        var rows = await DisplayNames.LookupAsync(data, lookup, DisplayNames.LookupTimeout, TestContext.Current.CancellationToken);

        Assert.Equal("Peter", Assert.Single(Assert.Single(rows!).Values));
    }

    // ---------- several display columns and one level deeper (WP-65) ----------

    private static readonly TableDetails Staat = new(new TableSummary("APP", "STAAT", TableKind.Table),
        [Col("CODE", "VARCHAR2"), Col("NAME", "VARCHAR2"), Col("KURZ", "VARCHAR2")], ["CODE"], [], false);

    // KUNDE2: 0 ID, 1 NAME, 2 STAAT_CODE → STAAT
    private static readonly TableDetails Kunde2 = new(new TableSummary("APP", "KUNDE2", TableKind.Table),
        [Col("ID", "NUMBER"), Col("NAME", "VARCHAR2"), Col("STAAT_CODE", "VARCHAR2")], ["ID"], [], false);

    private static readonly DisplayLink StaatLink =
        new(2, new ForeignKeyInfo("FK_STAAT", Kunde2.Table.Ref, ["STAAT_CODE"], Staat.Table.Ref, ["CODE"], FkSource.Declared), Staat, 0,
            [new DisplayPart(1), new DisplayPart(2)]);

    /// <summary>AUFTRAG.KUNDE_ID → KUNDE2, whose name is its STAAT (looked up in STAAT) and its NAME.</summary>
    private static readonly DisplayLink DeepLink =
        new(1, Fk("KUNDE_ID", Kunde2, "ID"), Kunde2, 0, [new DisplayPart(2, StaatLink), new DisplayPart(1)]);

    [Fact]
    public void A_name_joins_its_parts_and_leaves_out_null_ones()
    {
        var names = Names(KundeLink with { Parts = [new DisplayPart(1), new DisplayPart(2)] });
        var lookup = Assert.Single(names.Missing([Row(1m), Row(2m), Row(3m)]));

        names.Found(lookup, [new LookupRow(1m, ["Peters Pizza", "PP   "]), new LookupRow(2m, [null, "MS"]), new LookupRow(3m, [null, null])]);

        Assert.Equal("Peters Pizza - PP", names.NameOf(1, 1m)?.Text); // CHAR padding is not part of the name
        Assert.Equal("MS", names.NameOf(1, 2m)?.Text);
        Assert.Equal(new LookedUpName(null, false), names.NameOf(1, 3m));
    }

    [Fact]
    public void A_name_of_several_parts_is_shortened_as_a_whole()
    {
        var names = Names(KundeLink with { Parts = [new DisplayPart(1), new DisplayPart(2)] });
        var lookup = Assert.Single(names.Missing([Row(1m)]));

        names.Found(lookup, [new LookupRow(1m, [new string('a', 40), new string('b', 40)])]);

        var text = names.NameOf(1, 1m)!.Text!;
        Assert.Equal(DisplayNames.MaxTextLength, text.Length);
        Assert.Contains("a - b", text);
        Assert.EndsWith("…", text);
    }

    [Fact]
    public void Compose_joins_with_the_separator_and_gives_null_for_nothing()
    {
        Assert.Equal("Müller - Hans", DisplayNames.Compose(["Müller", null, " ", " Hans "]));
        Assert.Null(DisplayNames.Compose([null, ""]));
    }

    [Fact]
    public void A_part_one_level_deeper_shows_the_value_until_its_name_is_found()
    {
        var names = Names(DeepLink);
        var first = Assert.Single(names.Missing([Row(1m), Row(2m), Row(3m)]));

        names.Found(first, [new LookupRow(1m, ["DE", "Peter"]), new LookupRow(2m, ["XX", "Maria"]), new LookupRow(3m, ["DE", "Paul"])]);

        Assert.Equal("DE - Peter", names.NameOf(1, 1m)?.Text);
        var deeper = Assert.Single(names.Missing([]));
        Assert.Equal((0, StaatLink), (deeper.Part, deeper.Source));
        Assert.Equal(["DE", "XX"], deeper.Keys); // each value once
        Assert.True(names.Touches(deeper, Row(3m)));
        Assert.False(names.Touches(deeper, Row(4m)));

        names.Found(deeper, [new LookupRow("DE", ["Deutschland", "D"])]);

        Assert.Equal("Deutschland - D - Peter", names.NameOf(1, 1m)?.Text);
        Assert.Equal("XX - Maria", names.NameOf(1, 2m)?.Text); // not found there: the value
        Assert.Empty(names.Missing([Row(1m)]));
    }

    [Fact]
    public void A_failed_lookup_one_level_deeper_switches_only_that_part_off()
    {
        var names = Names(DeepLink);
        names.Found(Assert.Single(names.Missing([Row(1m)])), [new LookupRow(1m, ["DE", "Peter"])]);
        var deeper = Assert.Single(names.Missing([]));

        Assert.False(names.TimedOut(deeper, "zu langsam"));
        names.Failed(deeper, "ORA-00942");

        Assert.True(names.Shows(1));
        Assert.False(names.Wants(deeper));
        Assert.Equal("DE - Peter", names.NameOf(1, 1m)?.Text);
        names.Found(Assert.Single(names.Missing([Row(2m)])), [new LookupRow(2m, ["PL", "Jan"])]);
        Assert.Empty(names.Missing([])); // nothing asked one level deeper any more
    }

    [Fact]
    public void A_dropped_lookup_one_level_deeper_is_asked_for_again()
    {
        var names = Names(DeepLink);
        names.Found(Assert.Single(names.Missing([Row(1m)])), [new LookupRow(1m, ["DE", "Peter"])]);
        var deeper = Assert.Single(names.Missing([]));

        names.Dropped(deeper);
        var again = Assert.Single(names.Missing([]));
        names.Found(again, [new LookupRow("DE", ["Deutschland", null])]);
        var copy = names.Copy();
        names.Clear();

        Assert.Equal(["DE"], again.Keys);
        Assert.Equal("Deutschland - Peter", copy.NameOf(1, 1m)?.Text);
        Assert.Null(names.NameOf(1, 1m));
    }

    [Fact]
    public async Task Filling_looks_up_one_level_deeper_too()
    {
        var lookup = Substitute.For<IRowLookup>();
        lookup.LookupAsync(Kunde2, 0, Arg.Any<IReadOnlyList<int>>(), Arg.Any<IReadOnlyList<object>>(), Arg.Any<CancellationToken>())
            .Returns([new LookupRow(1m, ["DE", "Peter"])]);
        lookup.LookupAsync(Staat, 0, Arg.Is<IReadOnlyList<int>>(c => c.SequenceEqual(new[] { 1, 2 })), Arg.Any<IReadOnlyList<object>>(), Arg.Any<CancellationToken>())
            .Returns([new LookupRow("DE", ["Deutschland", "D"])]);
        var names = Names(DeepLink);

        var failures = await names.FillAsync(lookup, [Row(1m)], TestContext.Current.CancellationToken);

        Assert.Empty(failures);
        Assert.Equal("Deutschland - D - Peter", names.NameOf(1, 1m)?.Text);
    }

    /// <summary>
    /// Review WP-65: a name is put together once, when it is found – a copy for the export in the background never calls
    /// the grid's formatter (it is not for other threads), also for a link one level deeper.
    /// </summary>
    [Fact]
    public void A_copy_never_formats_and_a_name_is_formatted_once()
    {
        var calls = 0;
        var names = new DisplayNames([KundeLink with { Parts = [new DisplayPart(1), new DisplayPart(2)] }, DeepLink with { Column = 2 }], (_, _, value) =>
        {
            calls++;
            return value as string;
        });
        var lookups = names.Missing([Row(1m, 7m)]);
        names.Found(lookups[0], [new LookupRow(1m, ["Peter", "PP"])]);
        names.Found(lookups[1], [new LookupRow(7m, ["DE", "Paul"])]);
        names.Found(Assert.Single(names.Missing([])), [new LookupRow("DE", ["Deutschland", null])]);
        var copy = names.Copy();
        var formatted = calls;

        Assert.Equal("Peter - PP", names.NameOf(1, 1m)?.Text);
        Assert.Equal("Peter - PP", copy.NameOf(1, 1m)?.Text);
        Assert.Equal("Deutschland - Paul", copy.NameOf(2, 7m)?.Text);
        Assert.Equal("Deutschland - Paul", names.NameOf(2, 7m)?.Text);
        Assert.Equal(formatted, calls);
    }

    [Fact]
    public void Timeouts_in_a_row_one_level_deeper_switch_only_that_part_off()
    {
        var names = Names(DeepLink);
        names.Found(Assert.Single(names.Missing([Row(1m)])), [new LookupRow(1m, ["DE", "Peter"])]);
        var deeper = Assert.Single(names.Missing([]));

        Assert.False(names.TimedOut(deeper, "zu langsam"));
        Assert.False(names.TimedOut(deeper, "zu langsam"));
        Assert.True(names.Wants(deeper)); // still asked for: the lookup runs again
        Assert.True(names.TimedOut(deeper, "zu langsam"));

        Assert.False(names.Wants(deeper));
        Assert.True(names.Shows(1));
        Assert.Equal("DE - Peter", names.NameOf(1, 1m)?.Text);
    }
}
