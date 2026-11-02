using FerretSharp.Core.Schema;
using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>"Create constraint" at a relationship of the C# model: offered between tables, with a note where the target is no primary key.</summary>
public sealed class ModelConstraintOffersTests
{
    private static readonly TableSummary Auftrag = new("SHOP", "AUFTRAG", TableKind.Table);
    private static readonly TableSummary Kunde = new("SHOP", "KUNDE", TableKind.Table);
    private static readonly TableSummary KundeView = new("SHOP", "V_KUNDE", TableKind.View);
    private static readonly TableSummary Fremd = new("OTHER", "KUNDE", TableKind.Table, new SynonymInfo("SHOP", "FREMD_KUNDE"));

    private static readonly TableSummary[] Tables = [Auftrag, Kunde, KundeView, Fremd];

    /// <summary>
    /// Cases: the primary key (in another order) is ready; another column of a table is no primary key; a view, a table
    /// through a synonym or one not loaded on either side cannot get one.
    /// </summary>
    [Theory]
    [InlineData("AUFTRAG", "KUNDE", "MANDANT,ID", ModelConstraintOffer.Ready)]
    [InlineData("AUFTRAG", "KUNDE", "NUMMER", ModelConstraintOffer.NotPrimaryKey)]
    [InlineData("AUFTRAG", "KUNDE", "ID", ModelConstraintOffer.NotPrimaryKey)]
    [InlineData("AUFTRAG", "V_KUNDE", "MANDANT,ID", ModelConstraintOffer.NotTables)]
    [InlineData("V_KUNDE", "KUNDE", "MANDANT,ID", ModelConstraintOffer.NotTables)]
    [InlineData("AUFTRAG", "OTHER.KUNDE", "MANDANT,ID", ModelConstraintOffer.NotTables)]
    [InlineData("AUFTRAG", "GONE", "MANDANT,ID", ModelConstraintOffer.NotTables)]
    public void Both_sides_must_be_tables_and_a_target_other_than_the_primary_key_is_noted(string from, string to, string toColumns, ModelConstraintOffer expected)
    {
        static TableRef Ref(string name) => name.Contains('.', StringComparison.Ordinal)
            ? new TableRef(name.Split('.')[0], name.Split('.')[1])
            : new TableRef("SHOP", name);
        var columns = toColumns.Split(',');
        var relation = new ForeignKeyInfo("Auftrag.Kunde", Ref(from), [.. columns.Select(c => "KUNDE_" + c)], Ref(to), columns, FkSource.ClrModel);

        var offer = ModelConstraintOffers.Of(relation, r => Tables.FirstOrDefault(t => t.Ref == r),
            r => r == Kunde.Ref || r == KundeView.Ref || r == Fremd.Ref ? ["ID", "MANDANT"] : []);

        Assert.Equal(expected, offer);
    }
}
