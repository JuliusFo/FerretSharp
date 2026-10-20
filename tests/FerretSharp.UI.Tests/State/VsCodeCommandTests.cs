using FerretSharp.UI.Resources;
using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>Review of WP-40: VS Code starts without cmd.exe where it can, and refuses a path cmd would misread.</summary>
public sealed class VsCodeCommandTests
{
    private const string CodeCmd = @"C:\Users\me\AppData\Local\Programs\Microsoft VS Code\bin\code.cmd";
    private const string CodeExe = @"C:\Users\me\AppData\Local\Programs\Microsoft VS Code\Code.exe";

    [Fact]
    public void Code_exe_beside_the_bin_folder_takes_the_file_and_line_directly()
    {
        var start = VsCodeCommand.For(CodeCmd, @"C:\Repos\R&D\Kunde.cs", 12, path => path == CodeExe);

        Assert.Equal(CodeExe, start.Program);
        Assert.Equal(["-g", @"C:\Repos\R&D\Kunde.cs:12"], start.Arguments);
    }

    [Fact]
    public void Without_code_exe_a_plain_path_goes_through_code_cmd_and_one_with_cmd_characters_is_refused()
    {
        var plain = VsCodeCommand.For(CodeCmd, @"C:\Repos\Shop\Kunde.cs", null, _ => false);
        var special = VsCodeCommand.For(CodeCmd, @"C:\Repos\R&D\Kunde.cs", 12, _ => false);

        Assert.Equal(CodeCmd, plain.Program);
        Assert.Equal(["-g", @"C:\Repos\Shop\Kunde.cs"], plain.Arguments);
        Assert.Null(special.Program);
        Assert.Equal(ModelViewText.Usage_VsCodePath, special.Refusal);
        Assert.Equal(ModelViewText.Usage_NoVsCode, VsCodeCommand.For(null, @"C:\x.cs", 1).Refusal);
    }
}
