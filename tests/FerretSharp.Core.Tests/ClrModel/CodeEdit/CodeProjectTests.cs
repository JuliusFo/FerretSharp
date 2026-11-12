using FerretSharp.Core.ClrModel;
using FerretSharp.Testing;

namespace FerretSharp.Core.Tests.ClrModel;

/// <summary>The project a C# file of the work clone belongs to (WP-41; review 3.70.0, B5).</summary>
public sealed class CodeProjectTests : IDisposable
{
    private readonly TestFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private string Write(string relative, string text = "")
    {
        var path = _folder.Combine(relative.Split('/'));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void The_nearest_folder_with_project_files_decides_and_several_there_stay_unknown()
    {
        var app = Write("src/App/App.csproj");
        Directory.CreateDirectory(_folder.Combine("src", "App", "Entities"));
        Write("src/Two/A.csproj");
        Write("src/Two/B.csproj");
        Directory.CreateDirectory(_folder.Combine("loose"));

        Assert.Equal([app], CodeProject.Of(_folder.Path, _folder.Combine("src", "App", "Entities")));
        Assert.Equal(2, CodeProject.Of(_folder.Path, _folder.Combine("src", "Two"))!.Count); // the first in no fixed order before
        Assert.Empty(CodeProject.Of(_folder.Path, _folder.Combine("loose"))!);
    }

    /// <summary>An old project lists its files: a new file needs an entry. Found by a text search before, a comment counted.</summary>
    [Theory]
    [InlineData("<Project Sdk=\"Microsoft.NET.Sdk\"></Project>", true)]
    [InlineData("<Project><Sdk Name=\"Microsoft.NET.Sdk\" /></Project>", true)]
    [InlineData("<Project><Import Project=\"Sdk.props\" Sdk=\"Microsoft.NET.Sdk\" /></Project>", true)]
    [InlineData("<Project ToolsVersion=\"15.0\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\"><!-- Sdk=\"x\" --></Project>", false)]
    [InlineData("not xml", true)] // no warning on a guess
    public void An_sdk_project_is_read_as_xml(string text, bool sdk) => Assert.Equal(sdk, CodeProject.IsSdk(Write("App.csproj", text)));
}
