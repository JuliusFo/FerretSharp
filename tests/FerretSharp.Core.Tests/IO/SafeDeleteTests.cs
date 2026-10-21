using System.Text.RegularExpressions;
using FerretSharp.Core.IO;

namespace FerretSharp.Core.Tests.IO;

/// <summary>
/// Deleting a folder with its contents only ever below a fixed root – against the classic accident of a path built from
/// an empty or wrong value that takes a whole drive with it.
/// </summary>
public sealed partial class SafeDeleteTests : IDisposable
{
    private readonly TestFolder _folder = new();

    [Fact]
    public void Deletes_a_folder_below_the_root_with_its_contents()
    {
        var target = Directory.CreateDirectory(_folder.Combine("work", "sub")).Parent!.FullName;
        File.WriteAllText(_folder.Combine("work", "sub", "a.json"), "{}");

        SafeDelete.DirectoryBelow(_folder.Path, target);

        Assert.False(Directory.Exists(target));
        Assert.True(Directory.Exists(_folder.Path));
    }

    [Fact]
    public void A_missing_folder_is_nothing_to_do() =>
        SafeDelete.DirectoryBelow(_folder.Path, _folder.Combine("missing"));

    [Theory]
    [InlineData("")] // the root itself
    [InlineData("..")] // above it
    [InlineData("../other")] // beside it
    [InlineData("work/../..")]
    public void Refuses_anything_not_strictly_below_the_root(string relative)
    {
        var root = Directory.CreateDirectory(_folder.Combine("root")).FullName;
        var path = Path.GetFullPath(Path.Combine(root, relative));

        Assert.Throws<InvalidOperationException>(() => SafeDelete.DirectoryBelow(root, path));
        Assert.True(Directory.Exists(root));
    }

    [Fact]
    public void A_folder_whose_name_only_starts_like_the_root_is_not_below_it()
    {
        var root = Directory.CreateDirectory(_folder.Combine("data")).FullName;
        var sibling = Directory.CreateDirectory(_folder.Combine("data-backup")).FullName;

        Assert.Throws<InvalidOperationException>(() => SafeDelete.DirectoryBelow(root, sibling));
        Assert.True(Directory.Exists(sibling));
    }

    [Fact]
    public void Refuses_relative_paths_and_a_drive_root_as_root()
    {
        var driveRoot = Path.GetPathRoot(_folder.Path)!;

        Assert.Throws<InvalidOperationException>(() => SafeDelete.CheckBelow("", _folder.Path));
        Assert.Throws<InvalidOperationException>(() => SafeDelete.CheckBelow("work", Path.Combine("work", "sub")));
        Assert.Throws<InvalidOperationException>(() => SafeDelete.CheckBelow(_folder.Path, "sub"));
        Assert.Throws<InvalidOperationException>(() => SafeDelete.CheckBelow(driveRoot, _folder.Path));
    }

    [Fact]
    public void A_root_given_with_a_trailing_separator_works_the_same() =>
        Assert.Equal(
            Path.GetFullPath(_folder.Combine("work")),
            SafeDelete.CheckBelow(_folder.Path + Path.DirectorySeparatorChar, _folder.Combine("work") + Path.DirectorySeparatorChar));

    [Fact]
    public void Try_reports_a_refused_path_as_the_programming_error_it_is() =>
        Assert.Throws<InvalidOperationException>(() => SafeDelete.TryDirectoryBelow(_folder.Path, _folder.Path));

    /// <summary>
    /// Every recursive delete in code, tests and scripts goes through <see cref="SafeDelete"/> (tests: <c>TestFolder</c>);
    /// a new one elsewhere fails here.
    /// </summary>
    [Fact]
    public void Only_the_guarded_helpers_delete_folders()
    {
        var root = Repository.Root;
        var allowed = new[]
        {
            Path.Combine("src", "FerretSharp.Core", "IO", "SafeDelete.cs"),
            Path.Combine("tests", "FerretSharp.Core.Tests", "IO", "SafeDeleteTests.cs"), // the samples below
        };

        var offenders = SourceFiles(root)
            .Where(file => !allowed.Any(a => file.EndsWith(a, StringComparison.OrdinalIgnoreCase)))
            .SelectMany(file => File.ReadLines(file).Select((line, index) => (file, line, index)))
            .Where(l => DeletesFolders(l.file, l.line))
            .Select(l => $"{Path.GetRelativePath(root, l.file)}:{l.index + 1}: {l.line.Trim()}")
            .ToList();

        Assert.Empty(offenders);
    }

    [Theory]
    [InlineData("x.cs", "Directory.Delete(path, recursive: true);")]
    [InlineData("x.cs", "Directory.Delete(path);")]
    [InlineData("x.cs", "new DirectoryInfo(path).Delete(recursive: true);")]
    [InlineData("x.cs", "_root.Delete(true);")]
    [InlineData("x.ps1", "Remove-Item \"$dir\\*\" -Recurse -Force")]
    [InlineData("x.ps1", "rm -r $dir")]
    [InlineData("ci.yml", "run: rm -rf ./out")]
    [InlineData("x.targets", "<RemoveDir Directories=\"$(OutDir)\" />")]
    [InlineData("x.cmd", "rd /s /q %DIR%")]
    // Review 3.40.0, C12: abbreviated parameters, the aliases of Remove-Item, .NET from PowerShell, del /s, Python.
    [InlineData("x.ps1", "Remove-Item $dir -r -Force")]
    [InlineData("x.ps1", "Remove-Item -Path $dir -Rec")]
    [InlineData("x.ps1", "rmdir $dir -Recurse")]
    [InlineData("x.ps1", "ri $dir -Recurse -Force")]
    [InlineData("x.ps1", "del $dir -r")]
    [InlineData("x.ps1", "[IO.Directory]::Delete($p, $true)")]
    [InlineData("x.ps1", "[System.IO.Directory]::Delete($p)")]
    [InlineData("x.cmd", "del /s /q %DIR%\\*")]
    [InlineData("x.cmd", "erase /q /s %DIR%")]
    [InlineData("x.py", "shutil.rmtree(folder)")]
    [InlineData("x.py", "os.removedirs(folder)")]
    public void The_check_sees_folder_deletes(string file, string line) => Assert.True(DeletesFolders(file, line));

    [Theory]
    [InlineData("x.cs", "File.Delete(path);")]
    [InlineData("x.cs", "tracker.Delete(row);")]
    [InlineData("x.cs", "SafeDelete.DirectoryBelow(root, path);")]
    [InlineData("x.ps1", "Remove-Item $file")]
    [InlineData("x.ps1", "Remove-Item $file -Force")]
    [InlineData("x.cmd", "del /q %FILE%")]
    [InlineData("x.py", "os.remove(file)")]
    public void The_check_lets_other_deletes_pass(string file, string line) => Assert.False(DeletesFolders(file, line));

    private static bool DeletesFolders(string file, string line) => Path.GetExtension(file) switch
    {
        ".cs" or ".razor" => CSharpFolderDelete().IsMatch(line),
        ".py" => PythonFolderDelete().IsMatch(line),
        _ => ScriptFolderDelete().IsMatch(line),
    };

    [GeneratedRegex(@"\bDirectory\.Delete\s*\(|\.Delete\s*\(\s*(recursive\s*:\s*)?true\s*\)")]
    private static partial Regex CSharpFolderDelete();

    // PowerShell takes any unambiguous start of a parameter (-r, -Rec) and has aliases for Remove-Item (rm, rmdir, rd, ri,
    // del, erase); .NET's Directory.Delete is callable from it; cmd's del/erase /s delete a tree of files.
    [GeneratedRegex(
        @"\b(Remove-Item|rm|rmdir|rd|ri|del|erase)\b.*\s-r(e(c(u(r(s(e)?)?)?)?)?)?\b|\brm\s+-[a-zA-Z]*[rR]|\bRemoveDir\b|\b(rmdir|rd)\s+/[sS]"
        + @"|\b(del|erase)\s+(/[a-zA-Z]\s+)*/[sS]\b|\[(System\.)?IO\.Directory\]::Delete\s*\(",
        RegexOptions.IgnoreCase)]
    private static partial Regex ScriptFolderDelete();

    [GeneratedRegex(@"\bshutil\.rmtree\b|\bos\.removedirs\b|\bos\.rmdir\b|\.rmdir\s*\(")]
    private static partial Regex PythonFolderDelete();

    private static readonly string[] Extensions = [".cs", ".razor", ".ps1", ".psm1", ".sh", ".cmd", ".bat", ".yml", ".yaml", ".props", ".targets", ".csproj", ".py"];
    private static readonly string[] SkippedFolders = ["bin", "obj", ".git", ".vs", "artifacts", "node_modules", "lib"];

    private static IEnumerable<string> SourceFiles(string root) =>
        Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })
            .Where(f => Extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .Where(f => !Path.GetRelativePath(root, f).Split(Path.DirectorySeparatorChar).Any(SkippedFolders.Contains));

    public void Dispose() => _folder.Dispose();
}
