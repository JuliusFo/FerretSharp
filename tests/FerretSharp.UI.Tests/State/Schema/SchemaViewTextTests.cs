using FerretSharp.Core.Resources;
using FerretSharp.Core.Schema;
using FerretSharp.UI.Resources;
using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>Texts of the explorer and the tab views that the state classes compose (WP-29).</summary>
public sealed class SchemaViewTextTests
{
    [Fact]
    public void The_kind_of_a_PL_SQL_unit_follows_the_UI_language()
    {
        using (UiCulture.Use("en"))
        {
            Assert.Equal("Procedure", ExplorerList.KindLabel(PlSqlKind.Procedure));
            Assert.Equal("Function", ExplorerList.KindLabel(PlSqlKind.Function));
        }

        Assert.Equal("Prozedur", ExplorerList.KindLabel(PlSqlKind.Procedure));
        Assert.Equal("Funktion", ExplorerList.KindLabel(PlSqlKind.Function));
    }

    [Fact]
    public void What_INVALID_means_depends_on_the_object_type()
    {
        using (UiCulture.Use("en"))
        {
            Assert.StartsWith("INVALID: Oracle recompiles the view", InvalidStatusText.For("VIEW"));
            Assert.StartsWith("INVALID: needs to be recompiled", InvalidStatusText.For("MATERIALIZED VIEW"));
            Assert.StartsWith("INVALID: recompiled on the next call", InvalidStatusText.For("PACKAGE BODY"));
        }

        Assert.StartsWith("Ungültig (INVALID): Oracle kompiliert die View", InvalidStatusText.For("VIEW"));
    }

    [Theory]
    [InlineData(1, "1 hit in PL/SQL", "1 Treffer unter PL/SQL")]
    [InlineData(3, "3 hits in PL/SQL", "3 Treffer unter PL/SQL")]
    public void Counts_use_the_singular_only_for_one(int count, string english, string german)
    {
        using (UiCulture.Use("en"))
        {
            Assert.Equal(english, TextFormat.Plural(count, SchemaViewText.Explorer_HitsInPlSqlOne, SchemaViewText.Explorer_HitsInPlSqlOther));
        }

        Assert.Equal(german, TextFormat.Plural(count, SchemaViewText.Explorer_HitsInPlSqlOne, SchemaViewText.Explorer_HitsInPlSqlOther));
    }
}
