using FerretSharp.Core.Schema;
using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>WP-61: the head of a column menu.</summary>
public class ColumnFactsTests
{
    private static readonly TableRef Auftrag = new("APP", "AUFTRAG");
    private static readonly TableRef Kunde = new("APP", "KUNDE");
    private static readonly TableRef Mandant = new("APP", "MANDANT");

    private static ForeignKeyInfo Fk(string name, string[] from, TableRef to, FkSource source = FkSource.Declared) =>
        new(name, Auftrag, from, to, [.. from.Select(_ => "ID")], source);

    [Fact]
    public void The_foreign_keys_over_a_column_name_each_target_once_the_first_kept()
    {
        var constraint = Fk("FK_AUFTRAG_KUNDE", ["KUNDE_ID"], Kunde);
        var model = Fk("Kunde", ["KUNDE_ID"], Kunde, FkSource.ClrModel);
        var composite = Fk("FK_AUFTRAG_MANDANT", ["MANDANT_ID", "KUNDE_ID"], Mandant);
        var other = Fk("FK_AUFTRAG_BEARBEITER", ["BEARBEITER_ID"], new("APP", "MITARBEITER"));

        Assert.Equal([constraint, composite], ColumnFacts.ForeignKeysOf([constraint, other, model, composite], "KUNDE_ID"));
        Assert.Empty(ColumnFacts.ForeignKeysOf([constraint], "NAME"));
    }

    [Fact]
    public void The_title_names_the_relationship_and_one_of_the_csharp_model()
    {
        Assert.Equal("FK_AUFTRAG_KUNDE: APP.AUFTRAG(KUNDE_ID) → APP.KUNDE(ID)", ColumnFacts.Title(Fk("FK_AUFTRAG_KUNDE", ["KUNDE_ID"], Kunde)));
        Assert.Contains(Environment.NewLine, ColumnFacts.Title(Fk("Kunde", ["KUNDE_ID"], Kunde, FkSource.ClrModel)));
    }
}
