using FerretSharp.Core.Connections;
using FerretSharp.Core.Data;
using FerretSharp.Core.Links;
using FerretSharp.Core.Query;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Schema;
using FerretSharp.Core.Workspaces;

namespace FerretSharp.Core.Tests.Links;

/// <summary>Links to a table view (WP-37, ADR 0026): writing, reading foreign input, rows by key, finding the connection.</summary>
public class TableLinkTests
{
    private static ConnectionProfile Profile(string name, OracleAddress address, string user = "erp", string? schema = null) =>
        new(Guid.NewGuid(), name, ConnectionKind.Test, address, user, schema, false);

    private static readonly ConnectionProfile ErpTest = Profile("ERP Test", new HostPortAddress("db01", 1521, "ERPTEST", null));

    private static ColumnInfo Column(string name, string type, int position) =>
        new(name, type, type == "VARCHAR2" ? 50 : null, false, null, null, true, IsIdentity: false, Default: null, Position: position);

    private static readonly TableDetails Auftrag = new(new TableSummary("ERP", "AUFTRAG", TableKind.Table),
        [Column("AUFTRAG_ID", "NUMBER", 1), Column("STATUS", "VARCHAR2", 2)], ["AUFTRAG_ID"], [], false);

    private static TableLink Read(string text)
    {
        var read = TableLink.Parse(text);
        Assert.Null(read.Error);
        return read.Link!;
    }

    private static string Error(string text)
    {
        var read = TableLink.Parse(text);
        Assert.Null(read.Link);
        return read.Error!;
    }

    [Fact]
    public void A_link_reads_back_what_was_written()
    {
        var link = TableLink.For(ErpTest, new TableRef("ERP", "AUFTRAG"), [
            FilterCondition.Of("STATUS", FilterOperator.In, "OFFEN", "NEU, mit Komma", "a&b=c"),
            FilterCondition.Of("NOTIZ", FilterOperator.Contains, "100% #1 + ä"),
            FilterCondition.Of("ERLEDIGT", FilterOperator.IsNull),
            FilterCondition.Of("ERSTELLT", FilterOperator.Between, "1.10.2026", "31.10.2026"),
            FilterCondition.Of("PRIO", FilterOperator.Equals, "1") with { Enabled = false },
        ], [new SortSpec("ERSTELLT", true), new SortSpec("AUFTRAG_ID", false)], TabMode.Constraints);

        var uri = link.ToUri();
        var read = Read(uri);

        Assert.StartsWith("ferretsharp://open?v=1&n=12&c=ERP%20Test&a=db01%3A1521%2FERPTEST&s=ERP&t=AUFTRAG&f=STATUS,in,OFFEN,NEU%2C%20mit%20Komma,a%26b%3Dc&", uri);
        Assert.EndsWith("&m=constraints&e=1", uri);
        Assert.Equal("ERP Test", read.Connection);
        Assert.True(read.Address!.SameAs(ErpTest.Address));
        Assert.Equal(("ERP", "AUFTRAG"), (read.Owner, read.Table));
        Assert.Equal(4, read.Filters.Count); // the disabled row filters nothing and is left out
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(link.Filters[i] with { Values = read.Filters[i].Values }, read.Filters[i]);
            Assert.Equal(link.Filters[i].Values, read.Filters[i].Values);
        }

        Assert.Equal(link.Sorts, read.Sorts);
        Assert.Equal(TabMode.Constraints, read.Mode);
    }

    [Theory]
    [InlineData("tns")]
    [InlineData("sid")]
    public void Addresses_of_every_kind_read_back(string kind)
    {
        OracleAddress address = kind == "tns" ? new TnsAliasAddress("ERP_PROD", @"C:\oracle") : new HostPortAddress("db02", 1522, null, "ERP");
        var link = TableLink.For(Profile("X", address), new TableRef("ERP", "AUFTRAG"), [], [], TabMode.Data);

        var read = Read(link.ToUri());

        Assert.True(read.Address!.SameAs(address));
        Assert.Null(read.Mode);
    }

    [Theory]
    [InlineData("FERRETSHARP://OPEN?v=1&s=ERP&t=AUFTRAG&m=DDL")] // letter case of scheme, host and names of the format
    [InlineData("  ferretsharp://open?t=AUFTRAG&v=1&s=ERP&unknown=1#fragment  ")] // order, unknown parameters, blanks
    [InlineData("ferretsharp://open?v=1&s=ERP&t=AUFTRAG&f=STATUS,equals,OFFEN+NEU")] // + as a blank, as forms write it
    public void Links_written_by_hand_or_other_programs_are_read(string text)
    {
        var link = Read(text);

        Assert.Equal("AUFTRAG", link.Table);
        Assert.Null(link.Connection);
        Assert.Null(link.Address);
    }

    [Theory]
    [InlineData("", "kein FerretSharp-Link")]
    [InlineData("https://example.com/open?v=1&s=ERP&t=X", "kein FerretSharp-Link")]
    [InlineData("ferretsharp://delete?v=1&s=ERP&t=X", "kein FerretSharp-Link")]
    [InlineData("ferretsharp://open?s=ERP&t=X", "keine gültige Version")]
    [InlineData("ferretsharp://open?v=3&s=ERP&t=X", "neueren FerretSharp-Version (Format 3)")]
    [InlineData("ferretsharp://open?v=1&t=X", "nennt keine Tabelle")]
    [InlineData("ferretsharp://open?v=1&s=ERP&t=A%22B", "\"")]
    [InlineData("ferretsharp://open?v=1&s=ERP&t=X&f=STATUS,drop,1", "ungültigen Filter auf STATUS")]
    [InlineData("ferretsharp://open?v=1&s=ERP&t=X&f=STATUS,3,1", "ungültigen Filter auf STATUS")] // enum numbers are no names
    [InlineData("ferretsharp://open?v=1&s=ERP&t=X&f=STATUS,equals", "ungültigen Filter auf STATUS")] // value missing
    [InlineData("ferretsharp://open?v=1&s=ERP&t=X&f=STATUS,isNull,1", "ungültigen Filter auf STATUS")] // value too many
    [InlineData("ferretsharp://open?v=1&s=ERP&t=X&f=STATUS,in,A%3BB", "ungültigen Filter auf STATUS")] // the filter bar would split it
    [InlineData("ferretsharp://open?v=1&s=ERP&t=X&o=STATUS,up", "ungültige Sortierung auf STATUS")]
    [InlineData("ferretsharp://open?v=1&s=ERP&t=X&m=sql", "unbekannte Ansicht")]
    [InlineData("ferretsharp://open?v=2&s=ERP&t=X&g=0,any,STATUS,isNull", "ungültigen Filter auf STATUS")] // group ids start at 1
    [InlineData("ferretsharp://open?v=2&s=ERP&t=X&g=1,all,STATUS,isNull", "ungültigen Filter auf STATUS")] // no such kind
    [InlineData("ferretsharp://open?v=2&s=ERP&t=X&g=1,any,STATUS,equals", "ungültigen Filter auf STATUS")] // the row as with f
    public void Foreign_input_is_refused_with_the_reason(string text, string error)
    {
        Assert.Contains(error, Error(text));
    }

    [Fact]
    public void Links_beyond_the_limits_are_refused()
    {
        var many = "ferretsharp://open?v=1&s=ERP&t=X" + string.Concat(Enumerable.Range(0, TableLink.MaxFilters + 1).Select(i => $"&f=C{i},isNull"));
        var values = "ferretsharp://open?v=1&s=ERP&t=X&f=ID,in," + string.Join(",", Enumerable.Range(0, TableLink.MaxValues + 1));
        var huge = "ferretsharp://open?v=1&s=ERP&t=X&f=ID,equals," + new string('x', TableLink.MaxLength);

        Assert.Contains("mehr als 50 Filter", Error(many));
        Assert.Contains("mehr als 1000 Filterwerte", Error(values));
        Assert.Contains("länger als", Error(huge));
    }

    /// <summary>Review of WP-37: cut to the limit, a link too long could read as another one with a shorter value.</summary>
    [Fact]
    public void A_link_too_long_stays_too_long_when_handed_over()
    {
        var huge = "ferretsharp://open?v=1&s=ERP&t=X&f=ID,equals," + new string('x', TableLink.MaxLength);
        var fits = "ferretsharp://open?v=1&s=ERP&t=X";

        Assert.Equal(TableLink.MaxLength + 1, TableLink.ForTransfer(huge).Length);
        Assert.Contains("länger als", Error(TableLink.ForTransfer(huge)));
        Assert.Same(fits, TableLink.ForTransfer(fits));
    }

    /// <summary>
    /// ADR 0026, addendum: chats make only https clickable. The redirect page puts <see cref="TableLink.UriStart"/> in front of
    /// the part after <c>#</c>; that gives the same link back, values with <c>#</c>, <c>&amp;</c> and commas included.
    /// </summary>
    [Fact]
    public void The_https_form_carries_the_link_after_the_hash()
    {
        var link = TableLink.For(ErpTest, new TableRef("ERP", "AUFTRAG"),
            [FilterCondition.Of("NOTIZ", FilterOperator.Contains, "100% #1 & a,b"), FilterCondition.Of("STATUS", FilterOperator.In, "OFFEN", "NEU")],
            [new SortSpec("ERSTELLT", true)], TabMode.Data);

        var web = link.ToWebUri();
        var fragment = new Uri(web).Fragment;
        var opened = TableLink.UriStart + fragment[1..]; // what pages/open/index.html does

        Assert.StartsWith("https://juliusfo.github.io/FerretSharp/open/#v=1&n=8&c=ERP%20Test&", web);
        Assert.Single(web, '#'); // a # in a value stays encoded, the page sees all of it
        Assert.Equal(link.ToUri(), opened);
        Assert.Equal(link.Filters.SelectMany(f => f.Values), Read(opened).Filters.SelectMany(f => f.Values));
    }

    /// <summary>
    /// Review 3.40.0, A26: a link cut off on the way (a mail in plain text, a ticket field) was read as another, valid link –
    /// a value shortened, an IN list without its last values. Links of older versions (without "n") open as before.
    /// </summary>
    [Fact]
    public void A_link_cut_off_anywhere_is_told_from_a_whole_one()
    {
        var uri = TableLink.For(ErpTest, new TableRef("ERP", "AUFTRAG"),
            [FilterCondition.Of("NAME", FilterOperator.StartsWith, "Müller"), FilterCondition.Of("STATUS", FilterOperator.In, "OFFEN", "NEU")],
            [new SortSpec("ERSTELLT", true)], TabMode.Data).ToUri();
        Assert.Null(TableLink.Parse(uri).Error);

        // Every cut after the count: at a separator, inside a value, inside the end marker.
        for (var cut = uri.IndexOf("&c=", StringComparison.Ordinal); cut < uri.Length; cut++)
        {
            Assert.Equal(QueryText.LinkIncomplete, TableLink.Parse(uri[..cut]).Error);
        }

        Assert.Equal(QueryText.LinkIncomplete, TableLink.Parse(uri.Replace("&n=8&", "&n=9&", StringComparison.Ordinal)).Error);
        var older = "ferretsharp://open?v=1&c=ERP%20Test&s=ERP&t=AUFTRAG&f=NAME,startsWith,M%C3%BCl";
        Assert.Equal("Mül", Assert.Single(Read(older).Filters).Values[0]); // as before: no way to tell
    }

    [Theory]
    [InlineData("ferretsharp://open?v=1", true)]
    [InlineData(" FerretSharp:anything", true)]
    [InlineData("--data-dir=C:\\x", false)]
    [InlineData(null, false)]
    public void Start_arguments_are_told_apart(string? text, bool isLink) => Assert.Equal(isLink, TableLink.IsLink(text));

    [Fact]
    public void A_link_to_rows_filters_on_the_primary_key()
    {
        RowData Row(int id) => new(new RowKey.PrimaryKey([id]), [id, "OFFEN"]);

        var one = TableLink.ForRows(ErpTest, Auftrag, [Row(4711)]);
        var several = TableLink.ForRows(ErpTest, Auftrag, [Row(4711), Row(4712)]);
        var rowId = TableLink.ForRows(ErpTest, Auftrag with { PrimaryKey = [] }, [Row(1)]);
        var disabledKey = TableLink.ForRows(ErpTest, Auftrag with { PrimaryKeyEnforced = false }, [Row(1)]);

        var only = Assert.Single(one.Link!.Filters);
        Assert.Equal(("AUFTRAG_ID", FilterOperator.Equals, "4711"), (only.Column, only.Op, string.Join(";", only.Values)));
        Assert.Equal((FilterOperator.In, "4711;4712"), (several.Link!.Filters[0].Op, string.Join(";", several.Link.Filters[0].Values)));
        Assert.Equal(("ERP", "AUFTRAG", "ERP Test"), (several.Link.Owner, several.Link.Table, several.Link.Connection));
        Assert.Contains("ROWID", rowId.Unavailable);
        Assert.Contains("ROWID", disabledKey.Unavailable);
    }

    [Fact]
    public void The_connection_is_found_by_name_and_address_then_by_address_then_by_name()
    {
        var address = new HostPortAddress("DB01", 1521, "erptest", null); // other letter case: the same database
        var mine = Profile("ERP Test", address);
        var colleaguesName = Profile("ERP Test", new HostPortAddress("db99", 1521, "OTHER", null));
        var otherSchema = Profile("ERP Test (lesend)", address, user: "reader");
        var link = Read(TableLink.For(Profile("erp test", new HostPortAddress("db01", 1521, "ERPTEST", null)), new TableRef("ERP", "AUFTRAG"), [], [], TabMode.Data).ToUri());

        Assert.Same(mine, LinkConnectionMatch.Find([colleaguesName, otherSchema, mine], link).Profile);
        // Without a connection of that name and address: by address, narrowed to the link's schema.
        Assert.Equal("Mein ERP", LinkConnectionMatch.Find([colleaguesName, otherSchema, mine with { Name = "Mein ERP" }], link).Profile?.Name);
        // Only the name fits: another database of that name is no match while the address finds one.
        Assert.Same(otherSchema, LinkConnectionMatch.Find([colleaguesName, otherSchema], link).Profile);
        var byNameOnly = LinkConnectionMatch.Find([colleaguesName], link);
        Assert.Same(colleaguesName, byNameOnly.Profile);
        // Review of WP-37: the same name at another address may be another database – the confirmation says so.
        Assert.True(byNameOnly.AddressDiffers);
        Assert.False(LinkConnectionMatch.Find([colleaguesName, otherSchema, mine], link).AddressDiffers);
        Assert.False(LinkConnectionMatch.Find([colleaguesName], link with { Address = null }).AddressDiffers);
    }

    [Fact]
    public void Several_fitting_connections_become_candidates_and_none_is_no_match()
    {
        var address = new HostPortAddress("db01", 1521, "ERPTEST", null);
        var a = Profile("A", address);
        var b = Profile("B", address);
        var link = Read("ferretsharp://open?v=1&a=db01%3A1521%2FERPTEST&s=ERP&t=AUFTRAG");

        var both = LinkConnectionMatch.Find([a, b], link);
        var none = LinkConnectionMatch.Find([Profile("C", new TnsAliasAddress("X", null))], link);

        Assert.Null(both.Profile);
        Assert.Equal([a, b], both.Candidates);
        Assert.Null(none.Profile);
        Assert.Empty(none.Candidates);
    }

    /// <summary>For the user's own choice: the fitting connections first, then those reading the link's schema, the rest not.</summary>
    [Fact]
    public void Suggestions_are_the_fitting_connections_then_those_of_the_links_schema()
    {
        var link = Read("ferretsharp://open?v=1&c=ERP%20Test&a=db01%3A1521%2FERPTEST&s=ERP&t=AUFTRAG");
        var fits = Profile("ERP Test", new HostPortAddress("db01", 1521, "ERPTEST", null));
        var prod = Profile("ERP Prod", new HostPortAddress("db02", 1521, "ERPPROD", null)) with { Kind = ConnectionKind.Prod };
        var dev = Profile("ERP Dev", new TnsAliasAddress("ERPDEV", null)) with { Kind = ConnectionKind.Dev };
        var reader = Profile("ERP (lesend)", new HostPortAddress("db02", 1521, "ERPPROD", null), user: "reader", schema: "erp");
        var other = Profile("Kasse", new HostPortAddress("db03", 1521, "KASSE", null), user: "kasse");

        Assert.Equal([fits, dev, reader, prod], LinkConnectionMatch.Suggestions([other, prod, reader, fits, dev], link));
        // Nothing fits: those of the schema only, in the order of the connection list.
        Assert.Equal([dev, reader, prod], LinkConnectionMatch.Suggestions([other, prod, reader, dev], link));
        Assert.Empty(LinkConnectionMatch.Suggestions([other], link));
    }
}
