using FerretSharp.Core.Design;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Tests.Design;

public class ForeignKeyTargetsTests
{
    private static readonly TableRef Auftrag = new("SHOP", "AUFTRAG");

    private static readonly Dictionary<TableRef, string[]> Keys = new()
    {
        [Auftrag] = ["ID"],
        [new("SHOP", "KUNDEN")] = ["ID"],
        [new("SHOP", "ARTIKEL")] = ["ARTIKEL_NR"],
        [new("SHOP", "LAGER")] = ["MANDANT", "LAGER_NR"],
        [new("SHOP", "T_WAEHRUNG")] = ["ID"],
        [new("SHOP", "ZAHLART")] = [],
        [new("SHOP", "LIEFER_ADRESSE")] = ["ID"],
    };

    private static IReadOnlyList<ForeignKeyTarget> Suggest(string[] columns, params ForeignKeyInfo[] relations) =>
        ForeignKeyTargets.Suggest(Auftrag, columns, Keys.Keys, t => Keys.GetValueOrDefault(t) ?? [], relations);

    private static ForeignKeyInfo Relation(TableRef to, string[] from, string[] toColumns, FkSource source = FkSource.ClrModel) =>
        new("FK", Auftrag, from, to, toColumns, source);

    [Theory]
    [InlineData("KUNDE_ID")]
    [InlineData("KUNDEID")]
    [InlineData("ID_KUNDE")]
    [InlineData("kunde_id")]
    [InlineData("KUNDEN_ID")]
    public void A_column_named_after_a_table_suggests_it_with_its_key(string column)
    {
        var target = Assert.Single(Suggest([column]));

        Assert.Equal(new TableRef("SHOP", "KUNDEN"), target.Table);
        Assert.Equal(["ID"], target.Key);
        Assert.Equal(ForeignKeyTargetReason.Convention, target.Reason);
        Assert.Same(target, ForeignKeyTargets.Clear([target]));
    }

    [Fact]
    public void Underscores_inside_the_name_may_differ()
    {
        var target = Assert.Single(Suggest(["LIEFERADRESSE_ID"]));

        Assert.Equal("LIEFER_ADRESSE", target.Table.Name);
    }

    [Fact]
    public void The_name_of_a_single_column_primary_key_suggests_its_table()
    {
        var target = Assert.Single(Suggest(["ARTIKEL_NR"]));

        Assert.Equal(new TableRef("SHOP", "ARTIKEL"), target.Table);
        Assert.Equal(["ARTIKEL_NR"], target.Key);
        Assert.Equal(ForeignKeyTargetReason.KeyName, target.Reason);
    }

    [Fact]
    public void A_prefix_before_the_table_name_counts_less()
    {
        var target = Assert.Single(Suggest(["WAEHRUNG_ID"]));

        Assert.Equal("T_WAEHRUNG", target.Table.Name);
        Assert.Equal(ForeignKeyTargetReason.PrefixedConvention, target.Reason);
    }

    [Theory]
    [InlineData("ID")] // the key of several tables
    [InlineData("DATUM")]
    [InlineData("PARENT_ID")]
    [InlineData("LAGER_ID")] // two-column key
    [InlineData("ZAHLART_ID")] // no key
    public void Other_names_and_tables_without_a_single_column_key_suggest_nothing(string column) =>
        Assert.Empty(Suggest([column]));

    [Fact]
    public void The_own_key_column_is_not_a_reference_to_the_own_table()
    {
        Assert.Empty(ForeignKeyTargets.Suggest(new("SHOP", "ARTIKEL"), ["ARTIKEL_NR"], Keys.Keys, t => Keys.GetValueOrDefault(t) ?? [], []));
    }

    [Fact]
    public void A_relationship_without_constraint_wins_and_gives_its_columns_in_key_order()
    {
        var lager = new TableRef("SHOP", "LAGER");
        var relation = Relation(lager, ["LAGER", "MANDANT_ID"], ["LAGER_NR", "MANDANT"]);

        var target = Assert.Single(Suggest(["MANDANT_ID", "LAGER"], relation));

        Assert.Equal(lager, target.Table);
        Assert.Equal(["MANDANT", "LAGER_NR"], target.Key);
        Assert.Equal(ForeignKeyTargetReason.Relation, target.Reason);
    }

    [Fact]
    public void A_relationship_beats_a_name_and_each_table_comes_once()
    {
        var kunden = new TableRef("SHOP", "KUNDEN");
        var artikel = new TableRef("SHOP", "ARTIKEL");

        var suggestions = Suggest(["KUNDE_ID"], Relation(artikel, ["KUNDE_ID"], ["ARTIKEL_NR"]), Relation(kunden, ["KUNDE_ID"], ["ID"]));

        Assert.Equal([artikel, kunden], suggestions.Select(s => s.Table));
        Assert.All(suggestions, s => Assert.Equal(ForeignKeyTargetReason.Relation, s.Reason));
        Assert.Null(ForeignKeyTargets.Clear(suggestions)); // two relationships: the user chooses
    }

    [Fact]
    public void Declared_relationships_and_other_columns_do_not_count()
    {
        var kunden = new TableRef("SHOP", "KUNDEN");

        Assert.Empty(Suggest(["DATUM"], Relation(kunden, ["DATUM"], ["ID"], FkSource.Declared)));
        Assert.Empty(Suggest(["DATUM"], Relation(kunden, ["KUNDE_ID"], ["ID"])));
        Assert.Empty(Suggest(["DATUM"], Relation(kunden, ["DATUM", "KUNDE_ID"], ["ID", "X"])));
    }

    [Fact]
    public void Clear_takes_only_the_one_best()
    {
        var a = new ForeignKeyTarget(new("S", "A"), ["ID"], ForeignKeyTargetReason.KeyName);
        var b = new ForeignKeyTarget(new("S", "B"), ["ID"], ForeignKeyTargetReason.KeyName);
        var c = new ForeignKeyTarget(new("S", "C"), ["ID"], ForeignKeyTargetReason.Convention);

        Assert.Same(a, ForeignKeyTargets.Clear([a, c]));
        Assert.Null(ForeignKeyTargets.Clear([a, b, c]));
        Assert.Null(ForeignKeyTargets.Clear([]));
    }

    [Fact]
    public void No_columns_no_suggestion() =>
        Assert.Empty(Suggest([]));
}
