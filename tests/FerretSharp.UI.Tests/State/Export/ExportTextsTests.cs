using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>The notices of copying and exporting rows (WP-29): one whole sentence per count and language.</summary>
public sealed class ExportTextsTests
{
    [Fact]
    public void Row_counts_read_singular_and_plural_in_both_languages()
    {
        Assert.Equal("1 Zeile", ExportActions.RowsText(1));
        Assert.Equal("3 Zeilen", ExportActions.RowsText(3));
        using (UiCulture.Use("en"))
        {
            Assert.Equal("1 row", ExportActions.RowsText(1));
            Assert.Equal("3 rows", ExportActions.RowsText(3));
        }
    }

    [Fact]
    public void Copy_notices_are_whole_sentences()
    {
        Assert.Equal("1 Zeile als Tabelle kopiert.", ExportActions.CopiedText(ExportCopy.Table, 1));
        Assert.Equal("2 Zeilen als INSERT kopiert.", ExportActions.CopiedText(ExportCopy.Insert, 2));
        using (UiCulture.Use("en"))
        {
            Assert.Equal("1 row copied as a table.", ExportActions.CopiedText(ExportCopy.Table, 1));
            Assert.Equal("2 rows copied as INSERT.", ExportActions.CopiedText(ExportCopy.Insert, 2));
            Assert.Equal("5 rows copied as C# objects.", ExportActions.CopiedText(ExportCopy.CSharpObjects, 5));
            Assert.Equal("1 row copied as HasData.", ExportActions.CopiedText(ExportCopy.HasData, 1));
        }
    }

    [Fact]
    public void The_grid_texts_for_JavaScript_follow_the_UI_language_of_their_creation()
    {
        Assert.Equal("{0} (kein Member)", new GridTexts().NoMember);
        using (UiCulture.Use("en"))
        {
            Assert.Equal("{0} (no member)", new GridOptions([], 0, false, null, true).Texts.NoMember);
        }
    }
}
