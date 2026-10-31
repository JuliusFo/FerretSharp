using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>WP-70: what the object menu offers to copy – each text once, in the order the chips show them.</summary>
public sealed class ObjectMenuFactsTests
{
    [Fact]
    public void Copy_offers_the_known_name_the_real_object_and_the_csharp_names_once_each()
    {
        Assert.Equal(["S_PRODUKT", "STAMM.PRODUKT", "Produkt", "db.Produkte"],
            ObjectMenuFacts.CopyTexts("STAMM", "PRODUKT", "S_PRODUKT", "Produkt", "db.Produkte"));
        Assert.Equal(["KUNDEN", "APP.KUNDEN"], ObjectMenuFacts.CopyTexts("APP", "KUNDEN", "KUNDEN"));
        Assert.Equal(["Kunden", "APP.\"Kunden\""], ObjectMenuFacts.CopyTexts("APP", "Kunden", "Kunden")); // quoted only where code needs it
    }
}
