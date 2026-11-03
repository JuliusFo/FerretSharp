using FerretSharp.Core.Schema;
using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>The referenced tables the designer loads for new foreign keys (code review of the model constraints).</summary>
public sealed class ReferencedTableLoadsTests
{
    private static readonly TableDetails Kunde = new(new TableSummary("SHOP", "KUNDE", TableKind.Table),
        [new ColumnInfo("ID", "NUMBER", null, false, 10, 0, false, false, null, 1, null), new ColumnInfo("LAND", "VARCHAR2", 2, false, null, null, true, false, null, 2, null)],
        ["ID"], [], false);

    private static readonly TableDetails Land = new(new TableSummary("SHOP", "LAND", TableKind.Table),
        [new ColumnInfo("CODE", "VARCHAR2", 2, false, null, null, false, false, null, 1, null)], ["CODE"], [], false);

    /// <summary>
    /// Asked for once, not again by itself after a failed load (every render asks); chosen again, it is loaded again –
    /// before, the foreign key kept no referenced columns until the designer was closed. A known table is never due.
    /// </summary>
    [Fact]
    public void A_table_is_loaded_once_and_again_when_chosen_again_unless_known()
    {
        var designer = new TableDesignerDraft(Kunde, []);
        var fk = designer.AddConstraint(ConstraintType.ForeignKey);
        designer.AddColumn(fk, "LAND");
        designer.Reference(fk, Land.Table.Ref);
        var loads = new ReferencedTableLoads();

        Assert.Equal([Land.Table.Ref], loads.Due(designer.UnknownReferences()));
        Assert.Empty(loads.Due(designer.UnknownReferences())); // the load failed: the next render does not retry

        loads.Again(Land.Table.Ref); // chosen again
        Assert.Equal([Land.Table.Ref], loads.Due(designer.UnknownReferences()));

        designer.Learn(Land);
        loads.Again(Land.Table.Ref);
        Assert.Empty(loads.Due(designer.UnknownReferences()));
        Assert.Equal(["CODE"], fk.ReferencedColumns);
    }
}
