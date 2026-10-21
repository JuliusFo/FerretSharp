using FerretSharp.Core.IO;
using FerretSharp.Testing;

namespace FerretSharp.Core.Tests.IO;

/// <summary>One search for programs (review 3.44.0, B8: dotnet and VS Code each searched PATH on their own).</summary>
public sealed class ExecutableSearchTests : IDisposable
{
    private readonly TestFolder _folder = new();

    [Fact]
    public void A_program_is_found_in_the_folders_after_path_too()
    {
        var bin = Directory.CreateDirectory(_folder.Combine("Microsoft VS Code", "bin")).FullName;
        var code = Path.Combine(bin, "ferret-test-code.cmd");
        File.WriteAllText(code, "");

        Assert.Equal(code, ExecutableSearch.FindOnPath("ferret-test-code.cmd", ["  ", $"\"{bin}\""]));
        Assert.Null(ExecutableSearch.FindOnPath("ferret-test-missing.cmd", [bin]));
    }

    public void Dispose() => _folder.Dispose();
}
