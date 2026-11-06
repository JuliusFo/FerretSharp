using FerretSharp.Core.Design;
using FerretSharp.Core.Schema;
using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>The lines of the table picker: suggestions first, then the rest – all or the matches – up to a limit.</summary>
public sealed class TablePickListTests
{
    private static TableSummary T(string name) => new("SHOP", name, TableKind.Table);

    private static readonly TableSummary[] Tables = [T("ARTIKEL"), T("KUNDE"), T("KUNDEN_ADRESSE"), T("LIEFER_ORT"), T("ZAHLART")];

    private static IReadOnlyList<string> KeyOf(TableRef table) => table.Name == "ZAHLART" ? [] : ["ID"];

    private static readonly ForeignKeyTarget Kunde = new(new("SHOP", "KUNDE"), ["ID"], ForeignKeyTargetReason.Convention);

    [Fact]
    public void Without_a_query_the_suggestions_come_first_and_the_rest_in_list_order()
    {
        var list = TablePickList.Of(Tables, " ", [Kunde, new(new("OTHER", "GONE"), ["ID"], ForeignKeyTargetReason.KeyName)], KeyOf);

        var suggested = Assert.Single(list.Suggested); // one not in the list is left out
        Assert.Equal(("KUNDE", ForeignKeyTargetReason.Convention), (suggested.Table.Name, suggested.Reason));
        Assert.Equal(["ARTIKEL", "KUNDEN_ADRESSE", "LIEFER_ORT", "ZAHLART"], list.Rest.Select(r => r.Table.Name));
        Assert.Equal([], list.Rest[^1].Key);
        Assert.Equal(["ID"], list.Rest[0].Key);
        Assert.Same(suggested, list.First);
    }

    [Theory]
    [InlineData("lieferort", new[] { "LIEFER_ORT" }, new string[0])] // underscores may be left out
    [InlineData("kunde", new[] { "KUNDEN_ADRESSE" }, new[] { "KUNDE" })] // a suggestion stays on top while it matches
    [InlineData("adr", new[] { "KUNDEN_ADRESSE" }, new string[0])] // a suggestion that does not match goes
    [InlineData("kun", new[] { "KUNDEN_ADRESSE" }, new[] { "KUNDE" })] // as good as the best other (start of the name): on top
    [InlineData("vertrag", new string[0], new string[0])]
    public void A_query_keeps_the_matches(string query, string[] rest, string[] suggested)
    {
        var list = TablePickList.Of(Tables, query, [Kunde], KeyOf);

        Assert.Equal(suggested, list.Suggested.Select(r => r.Table.Name));
        Assert.Equal(rest, list.Rest.Select(r => r.Table.Name));
        Assert.Equal(suggested.Concat(rest).FirstOrDefault(), list.First?.Table.Name);
    }

    /// <summary>Code review: Enter took a suggestion that barely matched over the table typed in full.</summary>
    [Fact]
    public void A_suggestion_that_matches_worse_than_another_table_takes_its_place_among_them()
    {
        var adresse = new ForeignKeyTarget(new("SHOP", "KUNDEN_ADRESSE"), ["KUNDE_NR"], ForeignKeyTargetReason.Relation);

        var list = TablePickList.Of(Tables, "kunde", [adresse], KeyOf);

        Assert.Empty(list.Suggested);
        Assert.Equal(["KUNDE", "KUNDEN_ADRESSE"], list.Rest.Select(r => r.Table.Name)); // exact before prefix
        Assert.Equal("KUNDE", list.First?.Table.Name);
        // It shows the key picking it takes – the relationship's, not the primary key (code review, second pass).
        Assert.Equal(["KUNDE_NR"], list.Rest[1].Key);
        Assert.Equal(ForeignKeyTargetReason.Relation, list.Rest[1].Reason);
    }

    [Fact]
    public void A_query_finds_a_table_by_its_entity_name()
    {
        var list = TablePickList.Of(Tables, "Payment", [], KeyOf, t => t.Name == "ZAHLART" ? "PaymentMethod" : null);

        Assert.Equal(["ZAHLART"], list.Rest.Select(r => r.Table.Name));
    }

    [Fact]
    public void Past_the_limit_the_rest_is_counted()
    {
        var many = Enumerable.Range(0, TablePickList.Limit + 7).Select(i => T($"T{i:000}")).ToList();

        var list = TablePickList.Of(many, null, [], KeyOf);

        Assert.Equal((TablePickList.Limit, 7), (list.Rest.Count, list.More));
    }
}
