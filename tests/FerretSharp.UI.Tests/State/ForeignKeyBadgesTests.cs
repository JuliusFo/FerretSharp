using FerretSharp.Core.Schema;
using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>R6b (review 3.40.0, B11): one rule for the FK badges of the table grid and the result grid.</summary>
public class ForeignKeyBadgesTests
{
    private static readonly TableRef Auftrag = new("APP", "AUFTRAG");

    private static ForeignKeyInfo Fk(string column, FkSource source) =>
        new("FK_" + column, Auftrag, [column], new("APP", "KUNDE"), ["ID"], source);

    [Fact]
    public void Declared_and_model_foreign_keys_over_the_column_show_their_badge_others_none()
    {
        ForeignKeyInfo[] outgoing = [Fk("KUNDE_ID", FkSource.Declared), Fk("BEARBEITER_ID", FkSource.ClrModel), Fk("LAND", FkSource.Manual)];

        Assert.Equal((true, false), ForeignKeyBadges.Of(outgoing, "KUNDE_ID"));
        Assert.Equal((false, true), ForeignKeyBadges.Of(outgoing, "BEARBEITER_ID"));
        Assert.Equal((false, false), ForeignKeyBadges.Of(outgoing, "LAND"));
        Assert.Equal((false, false), ForeignKeyBadges.Of(outgoing, "NAME"));
    }
}
