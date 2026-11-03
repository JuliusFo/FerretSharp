using FerretSharp.Core.Schema;
using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>"Create constraint" asks the tab's edit mode for foreign keys (code review of the model constraints).</summary>
public sealed class DesignerForeignKeyRequestsTests
{
    private static readonly TableSummary Auftrag = new("SHOP", "AUFTRAG", TableKind.Table);

    /// <summary>Two relationships asked for while the edit mode still loads both arrive – the second replaced the first.</summary>
    [Fact]
    public void Every_relationship_asked_for_before_the_designer_takes_them_arrives_once()
    {
        var tab = new TableTab(Guid.NewGuid(), Auftrag);
        var bearbeiter = new ForeignKeyInfo("Auftrag.Bearbeiter", Auftrag.Ref, ["BEARBEITER_ID"], new TableRef("SHOP", "MITARBEITER"), ["ID"], FkSource.ClrModel);
        var kunde = new ForeignKeyInfo("Auftrag.Kunde", Auftrag.Ref, ["KUNDE_ID"], new TableRef("SHOP", "KUNDE"), ["ID"], FkSource.ClrModel);

        tab.RequestForeignKey(bearbeiter);
        tab.RequestForeignKey(kunde);

        Assert.Equal([bearbeiter, kunde], tab.TakeForeignKeys());
        Assert.Empty(tab.TakeForeignKeys());
    }
}
