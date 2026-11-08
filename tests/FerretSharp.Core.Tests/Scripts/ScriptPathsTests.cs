using FerretSharp.Core.Connections;
using FerretSharp.Core.Scripts;

namespace FerretSharp.Core.Tests.Scripts;

/// <summary>WP-77: scripts are read, written and deleted only strictly below their folder.</summary>
public sealed class ScriptPathsTests : IDisposable
{
    private readonly TestFolder _folder = new();

    [Theory]
    [InlineData("Locks.sql", "Locks.sql")]
    [InlineData("Oracle/Locks.SQL", "Oracle/Locks.SQL")]
    [InlineData("Oracle\\Locks.sql", "Oracle/Locks.sql")]
    public void A_path_of_names_below_the_root_resolves(string relative, string expected)
    {
        Assert.Equal(_folder.Combine([.. expected.Split('/')]), ScriptPaths.Resolve(_folder.Path, relative));
    }

    [Theory]
    [InlineData("")]
    [InlineData("../x.sql")]
    [InlineData("a/../../x.sql")]
    [InlineData("./x.sql")]
    [InlineData("/x.sql")]
    [InlineData("\\\\server\\share\\x.sql")]
    [InlineData("C:\\x.sql")]
    [InlineData("C:x.sql")]
    [InlineData("x.txt")]
    [InlineData("CON.sql")]
    [InlineData(".git/x.sql")]
    [InlineData("a/b/c/d/e/f/g/h/i/x.sql")]
    [InlineData("a%APPDATA%/x.sql")]
    public void Any_other_path_is_refused(string relative)
    {
        Assert.Throws<RefusedException>(() => ScriptPaths.Resolve(_folder.Path, relative));
    }

    [Fact]
    public void A_relative_root_is_refused()
    {
        Assert.Throws<RefusedException>(() => ScriptPaths.Resolve("scripts", "x.sql"));
    }

    [Fact]
    public void A_link_below_the_root_is_not_followed()
    {
        var outside = _folder.Combine("outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.sql"), "SELECT 1 FROM dual");
        var root = _folder.Combine("root");
        Directory.CreateDirectory(root);
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(root, "linked"), outside);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("Creating a symbolic link needs developer mode or admin rights on Windows.");
        }

        Assert.Throws<RefusedException>(() => ScriptPaths.Resolve(root, "linked/secret.sql"));
        Assert.Throws<RefusedException>(() => ScriptPaths.Resolve(root, "linked/new.sql")); // also for a file that does not exist yet
    }

    /// <summary>Review WP-77, M2: a drive (a mapped share, <c>S:\</c>) as the folder refused every script below it.</summary>
    [Fact]
    public void A_drive_root_can_be_the_folder()
    {
        var drive = Path.GetPathRoot(_folder.Path)!;

        Assert.Equal(Path.Combine(drive, "x.sql"), ScriptPaths.Resolve(drive, "x.sql"));
        Assert.Equal("x.sql", ScriptPaths.RelativeOf(drive, Path.Combine(drive, "x.sql")));
    }

    /// <summary>Review WP-77, K7: a junction needs no rights on Windows – unlike a symbolic link, whose test is often skipped.</summary>
    [Fact]
    public void A_junction_below_the_root_is_not_followed()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Junctions exist only on Windows.");
        }

        var outside = _folder.Combine("outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.sql"), "SELECT 1 FROM dual");
        var root = _folder.Combine("root");
        Directory.CreateDirectory(root);
        using (var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
               {
                   ArgumentList = { "/c", "mklink", "/J", Path.Combine(root, "joined"), outside },
                   UseShellExecute = false,
                   RedirectStandardOutput = true,
                   CreateNoWindow = true,
               })!)
        {
            mklink.WaitForExit();
            Assert.Equal(0, mklink.ExitCode);
        }

        Assert.Throws<RefusedException>(() => ScriptPaths.Resolve(root, "joined/secret.sql"));
    }

    [Theory]
    [InlineData("Open orders", null)]
    [InlineData("", "missing")]
    [InlineData("  ", "missing")]
    [InlineData(".hidden", "dot")]
    [InlineData("a/b", "invalid")]
    [InlineData("a:b", "invalid")]
    [InlineData("name.", "invalid")]
    [InlineData("NUL", "invalid")]
    [InlineData("50%", "invalid")]
    public void A_name_from_the_dialog_is_checked_as_a_file_name(string name, string? problem)
    {
        var result = ScriptPaths.NameProblem(name);

        Assert.Equal(problem is null, result is null);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData(" /Locks\\Oracle/ ", "Locks/Oracle")]
    [InlineData("Locks / Oracle", "Locks/Oracle")]
    public void A_folder_from_the_dialog_is_normalized(string folder, string expected)
    {
        Assert.Equal(expected, ScriptPaths.Folder(folder));
    }

    [Theory]
    [InlineData("a/../b")]
    [InlineData("a/CON")]
    [InlineData("1/2/3/4/5/6/7/8/9")]
    public void A_folder_from_the_dialog_with_a_bad_part_is_refused(string folder)
    {
        Assert.Throws<RefusedException>(() => ScriptPaths.Folder(folder));
    }

    [Fact]
    public void Name_folder_and_relative_path_belong_together()
    {
        var path = ScriptPaths.Of("Locks/Oracle", "Sessions");

        Assert.Equal("Locks/Oracle/Sessions.sql", path);
        Assert.Equal("Locks/Oracle", ScriptPaths.FolderOf(path));
        Assert.Equal("Sessions", ScriptPaths.NameOf(path));
        Assert.Equal("x.sql", ScriptPaths.Of("", "x.sql"));
        Assert.Equal(path, ScriptPaths.RelativeOf(_folder.Path, ScriptPaths.Resolve(_folder.Path, path)));
        Assert.Null(ScriptPaths.RelativeOf(_folder.Combine("a"), _folder.Combine("b", "x.sql")));
    }

    public void Dispose() => _folder.Dispose();
}
