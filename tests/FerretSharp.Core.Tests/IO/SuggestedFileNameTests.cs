using FerretSharp.Core.IO;

namespace FerretSharp.Core.Tests.IO;

/// <summary>Review of WP-68: a quoted name with <c>\</c>, <c>/</c> or <c>..</c> moved the save dialog to another folder.</summary>
public class SuggestedFileNameTests
{
    [Theory]
    [InlineData("2026-11-01_KUNDEN.sql", "2026-11-01_KUNDEN.sql")]
    [InlineData("Kunden mit Leerzeichen.csv", "Kunden mit Leerzeichen.csv")]
    [InlineData("ÄÖÜ_ß.xlsx", "ÄÖÜ_ß.xlsx")]
    [InlineData("KUNDEN.ferretmodel.json", "KUNDEN.ferretmodel.json")]
    public void A_plain_name_stays(string name, string file) =>
        Assert.Equal(file, SuggestedFileName.Of(name));

    [Theory]
    [InlineData(@"2026-11-01_DROP_..\..\Startup\x.sql", "2026-11-01_DROP_.._.._Startup_x.sql")]
    [InlineData("2026-11-01_../../x.sql", "2026-11-01_.._.._x.sql")]
    [InlineData(@"C:\Windows\x.sql", "C__Windows_x.sql")]
    [InlineData(@"\\server\share\x.csv", "__server_share_x.csv")]
    [InlineData("A<B>C:D\"E|F?G*H.txt", "A_B_C_D_E_F_G_H.txt")]
    [InlineData("TAB\tNAME\n.sql", "TAB_NAME_.sql")]
    public void No_folder_and_no_character_Windows_refuses(string name, string file) =>
        Assert.Equal(file, SuggestedFileName.Of(name));

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("  ")]
    [InlineData("")]
    public void Nothing_left_is_an_underscore(string name) =>
        Assert.Equal("_", SuggestedFileName.Of(name));

    [Theory]
    [InlineData("NAME. ", "NAME")]
    [InlineData(" NAME.sql ", "NAME.sql")]
    public void Trailing_dots_and_blanks_go(string name, string file) =>
        Assert.Equal(file, SuggestedFileName.Of(name));

    [Theory]
    [InlineData("NUL.sql", "_NUL.sql")]
    [InlineData("con.txt", "_con.txt")]
    [InlineData("COM1.tar.gz", "_COM1.tar.gz")]
    [InlineData("LPT¹.csv", "_LPT¹.csv")]
    [InlineData("AUX", "_AUX")]
    [InlineData("NULL.sql", "NULL.sql")]
    [InlineData("CONSOLE.sql", "CONSOLE.sql")]
    public void A_device_name_gets_an_underscore(string name, string file) =>
        Assert.Equal(file, SuggestedFileName.Of(name));

    [Fact]
    public void A_long_name_is_cut_before_its_extension()
    {
        var file = SuggestedFileName.Of(new string('T', 128) + "_" + new string('C', 128) + ".txt");

        Assert.Equal(255, file.Length);
        Assert.EndsWith("C.txt", file);
    }
}
