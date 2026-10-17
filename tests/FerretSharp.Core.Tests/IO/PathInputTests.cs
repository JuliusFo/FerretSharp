using FerretSharp.Core.IO;

namespace FerretSharp.Core.Tests.IO;

/// <summary>Review 3.40.0, A19: a path from "Copy as path" worked for a group's audit template and was "missing" globally.</summary>
public class PathInputTests
{
    [Theory]
    [InlineData(@"""C:\Vorlagen\audit.sql""", @"C:\Vorlagen\audit.sql")]
    [InlineData(@"  C:\Vorlagen\audit.sql  ", @"C:\Vorlagen\audit.sql")]
    [InlineData(@" ""\\server\share\audit.sql"" ", @"\\server\share\audit.sql")]
    [InlineData(@"C:\Mit Leerzeichen\a.sql", @"C:\Mit Leerzeichen\a.sql")]
    public void Blanks_and_quotes_around_the_path_go(string typed, string path) =>
        Assert.Equal(path, PathInput.Normalize(typed));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"\"")]
    public void Nothing_left_is_no_path(string? typed) =>
        Assert.Null(PathInput.Normalize(typed));
}
