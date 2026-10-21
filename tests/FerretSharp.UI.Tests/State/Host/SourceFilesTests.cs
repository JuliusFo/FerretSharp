using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>
/// Review 3.44.0, A29: "open with the default program" handed any path from the PDB to the shell – a crafted <c>#line</c>
/// pointing at a <c>.bat</c> would have run it.
/// </summary>
public sealed class SourceFilesTests
{
    [Theory]
    [InlineData(@"C:\repo\Shop\Customer.cs", true)]
    [InlineData(@"C:\repo\Shop\Pages\Index.razor", true)]
    [InlineData(@"C:\repo\Shop\obj\Debug\Model.g.CS", true)]
    [InlineData(@"C:\repo\tools\x.bat", false)]
    [InlineData(@"C:\repo\tools\x.cmd", false)]
    [InlineData(@"C:\repo\link.lnk", false)]
    [InlineData(@"C:\repo\page.url", false)]
    [InlineData(@"C:\repo\Customer", false)]
    [InlineData(@"\\host\share\Customer.cs", false)] // an SMB sign-in to a foreign host
    public void Only_source_files_go_to_the_shell(string path, bool opens) =>
        Assert.Equal(opens, SourceFiles.MayOpenWithShell(path));
}
