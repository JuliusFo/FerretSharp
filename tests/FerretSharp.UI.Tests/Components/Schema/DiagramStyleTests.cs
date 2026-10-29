using FerretSharp.Core.Schema;
using FerretSharp.UI.Components;

namespace FerretSharp.UI.Tests.Components.Schema;

/// <summary>The "+n" handles of the diagrams: the colour of their chevrons and the kinds in their tooltip.</summary>
public sealed class DiagramStyleTests
{
    private static readonly TableRef Order = new("APP", "ORDERS");

    private static ForeignKeyInfo Fk(string name, FkSource source) =>
        new(name, Order, [name + "_ID"], new TableRef("APP", name), ["ID"], source);

    [Fact]
    public void A_handle_takes_the_colour_of_its_relationships_only_if_they_share_a_kind()
    {
        Assert.Equal("declared", DiagramStyle.HandleClass([Fk("CUSTOMER", FkSource.Declared), Fk("CLERK", FkSource.Declared)]));
        Assert.Equal("clr", DiagramStyle.HandleClass([Fk("ARTICLE", FkSource.ClrModel)]));
        Assert.Equal("other", DiagramStyle.HandleClass([Fk("A", FkSource.Manual), Fk("B", FkSource.Convention)])); // one colour
        Assert.Equal("", DiagramStyle.HandleClass([Fk("CUSTOMER", FkSource.Declared), Fk("ARTICLE", FkSource.ClrModel)]));
        Assert.Equal("", DiagramStyle.HandleClass([]));
    }

    [Fact]
    public void The_tooltip_of_a_handle_counts_its_relationships_per_kind()
    {
        ForeignKeyInfo[] relations = [Fk("ARTICLE", FkSource.ClrModel), Fk("CUSTOMER", FkSource.Declared), Fk("CLERK", FkSource.Declared)];

        Assert.Equal("Show" + Environment.NewLine + "Beziehungen: Constraints 2 · C#-Modell 1", DiagramStyle.HandleTitle("Show", relations));
        using (UiCulture.Use("en"))
        {
            Assert.Equal("Show" + Environment.NewLine + "Relationships: Constraints 2 · C# model 1", DiagramStyle.HandleTitle("Show", relations));
        }

        Assert.Equal("Show", DiagramStyle.HandleTitle("Show", []));
    }
}
