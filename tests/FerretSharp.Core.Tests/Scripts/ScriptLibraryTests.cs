using FerretSharp.Core.Connections;
using FerretSharp.Core.Query;
using FerretSharp.Core.Scripts;
using FerretSharp.Core.Settings;

namespace FerretSharp.Core.Tests.Scripts;

/// <summary>WP-77: the scripts of the personal, group and general folders as the explorer lists them.</summary>
public sealed class ScriptLibraryTests : IDisposable
{
    private readonly TestFolder _folder = new();
    private readonly List<ScriptLibrary> _libraries = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Personal => _folder.Combine("data", "scripts");

    private (ScriptLibrary Library, AppSettingsService Settings) Library(AppSettings? settings = null)
    {
        var service = new AppSettingsService(new SettingsStore(_folder.Combine("settings.json")), settings ?? AppSettings.Default);
        var library = new ScriptLibrary(service, Personal, watch: false);
        _libraries.Add(library);
        return (library, service);
    }

    private void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Fact]
    public async Task A_connection_sees_the_personal_folder_its_group_s_and_the_general_one()
    {
        var erp = _folder.Combine("repo", "erp-scripts");
        var shop = _folder.Combine("repo", "shop-scripts");
        var general = _folder.Combine("repo", "oracle");
        Write(Path.Combine(Personal, "Mine.sql"), "SELECT 1 FROM dual");
        Write(Path.Combine(erp, "Orders", "Open.sql"), "-- @description Open orders\n-- @param id number\nSELECT * FROM orders WHERE id = :id");
        Write(Path.Combine(shop, "Carts.sql"), "SELECT 1 FROM dual");
        Write(Path.Combine(general, "Locks.sql"), "SELECT * FROM v$lock");
        var (library, _) = Library(AppSettings.Default with
        {
            SqlScriptsGroups = SettingsMap.Empty.With("ERP", erp).With("Shop", shop),
            SqlScriptsGeneralPath = $"\"{general}\"",
        });
        var profile = new ConnectionProfile(Guid.NewGuid(), "erp-dev", ConnectionKind.Dev, new HostPortAddress("h", 1521, "S", null), "u", null, ReadOnly: false, Group: " erp ");

        await library.RefreshAsync(Ct);
        var scans = library.For(profile);

        Assert.Equal([ScriptRootKind.Personal, ScriptRootKind.Group, ScriptRootKind.General], scans.Select(s => s.Root.Kind));
        Assert.Equal("ERP", scans[1].Root.Group);
        Assert.Equal(["Mine.sql"], scans[0].Scripts.Select(s => s.Path));
        var open = Assert.Single(scans[1].Scripts);
        Assert.Equal(("Orders/Open.sql", "Open", "Orders", "Open orders"), (open.Path, open.Name, open.Folder, open.Description));
        Assert.Equal([new ScriptParameter("id", SqlVariableType.Number)], open.Parameters);
        Assert.Equal(["Orders"], scans[1].Folders);
        Assert.Equal(["Locks.sql"], scans[2].Scripts.Select(s => s.Path));
        Assert.Equal(2, library.For(null).Count); // without a connection: no group
        Assert.Same(open, library.Find(scans[1].Root, "orders/open.SQL"));
    }

    [Fact]
    public async Task Hidden_folders_links_other_files_and_names_a_save_would_refuse_are_not_listed()
    {
        Write(Path.Combine(Personal, "a.sql"), "SELECT 1 FROM dual");
        Write(Path.Combine(Personal, ".git", "b.sql"), "SELECT 1 FROM dual");
        Write(Path.Combine(Personal, "notes.txt"), "x");
        Write(Path.Combine(Personal, "50%.sql"), "SELECT 1 FROM dual");
        Write(Path.Combine(Personal, "Deep", "1", "2", "3", "4", "5", "6", "7", "8", "too-deep.sql"), "SELECT 1 FROM dual");
        var (library, _) = Library();

        await library.RefreshAsync(Ct);

        var scan = Assert.Single(library.For(null));
        Assert.Equal(["a.sql"], scan.Scripts.Select(s => s.Path));
        Assert.DoesNotContain(scan.Folders, f => f.StartsWith(".git", StringComparison.Ordinal));
        Assert.Contains("Deep/1/2/3/4/5/6/7", scan.Folders);
        Assert.DoesNotContain("Deep/1/2/3/4/5/6/7/8", scan.Folders);
    }

    [Fact]
    public async Task A_missing_personal_folder_is_empty_and_a_missing_shared_one_an_error()
    {
        var (library, _) = Library(AppSettings.Default with { SqlScriptsGeneralPath = _folder.Combine("nowhere") });
        Assert.All(library.For(null), s => Assert.False(s.Loaded));

        await library.RefreshAsync(Ct);

        var scans = library.For(null);
        Assert.Null(scans[0].Error);
        Assert.Empty(scans[0].Scripts);
        Assert.Contains("nowhere", scans[1].Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Problems_of_a_file_show_in_its_entry_and_a_change_is_reported_once()
    {
        Write(Path.Combine(Personal, "a.sql"), "-- @param x varchar\nSELECT 1 FROM dual");
        var (library, _) = Library();
        var changes = 0;
        library.Changed += () => changes++;

        await library.RefreshAsync(Ct);
        await library.RefreshAsync(Ct);
        Assert.Equal(1, changes);
        Assert.Single(Assert.Single(library.For(null)[0].Scripts).Problems);

        Write(Path.Combine(Personal, "a.sql"), "-- @description fixed\n-- @param x text\nSELECT :x FROM dual");
        File.SetLastWriteTimeUtc(Path.Combine(Personal, "a.sql"), DateTime.UtcNow.AddMinutes(1));
        await library.RefreshAsync(Ct);

        Assert.Equal(2, changes);
        var entry = Assert.Single(library.For(null)[0].Scripts);
        Assert.Empty(entry.Problems);
        Assert.Equal("fixed", entry.Description);
    }

    /// <summary>
    /// Review WP-77, K4 and second pass: a group's folder set by hand in quotes is the same root the library reads, and
    /// one of only quotes is none (it gave a root without path, which broke the explorer).
    /// </summary>
    [Theory]
    [InlineData("\"C:\\repo\\erp\"", true)]
    [InlineData("\"\"", false)]
    [InlineData("  ", false)]
    public void A_group_folder_in_quotes_is_the_folder_and_quotes_alone_are_none(string value, bool listed)
    {
        var settings = AppSettings.Default with { SqlScriptsGroups = SettingsMap.Empty.With("ERP", value) };
        var profile = new ConnectionProfile(Guid.NewGuid(), "erp-dev", ConnectionKind.Dev, new HostPortAddress("h", 1521, "S", null), "u", null, ReadOnly: false, Group: "ERP");

        var group = ScriptRoots.For(settings, Personal, profile).Where(r => r.Kind == ScriptRootKind.Group).ToList();

        Assert.Equal(listed, group.Count == 1);
        Assert.Equal(
            group.Select(r => r.Path),
            ScriptRoots.All(settings, Personal).Where(r => r.Kind == ScriptRootKind.Group).Select(r => r.Path));
    }

    [Fact]
    public void A_folder_set_twice_is_listed_once()
    {
        var settings = AppSettings.Default with { SqlScriptsPath = _folder.Combine("same"), SqlScriptsGeneralPath = _folder.Combine("same") + Path.DirectorySeparatorChar };

        var roots = ScriptRoots.For(settings, Personal, null);

        Assert.Equal(ScriptRootKind.Personal, Assert.Single(roots).Kind);
        Assert.Equal(2, ScriptRoots.All(settings, Personal).Count); // the library reads both; the key tells them apart
    }

    /// <summary>
    /// Review WP-77, second pass: a share hanging over the VPN held back the reads of all other folders, and a refresh
    /// asked for while it was read could get lost – a script just saved showed only at the next look.
    /// </summary>
    [Fact]
    public async Task A_hanging_folder_holds_back_only_itself_and_a_request_meanwhile_is_read_after_it()
    {
        var general = _folder.Combine("share");
        Write(Path.Combine(general, "a.sql"), "SELECT 1 FROM dual");
        var (library, _) = Library(AppSettings.Default with { SqlScriptsGeneralPath = general });
        using var hanging = new ManualResetEventSlim();
        using var arrived = new ManualResetEventSlim();
        library.AfterRead = root =>
        {
            if (root.Kind == ScriptRootKind.General)
            {
                arrived.Set();
                hanging.Wait(TestContext.Current.CancellationToken);
            }
        };

        var first = library.RefreshAsync(Ct);
        Assert.True(arrived.Wait(TimeSpan.FromSeconds(10), Ct));
        Write(Path.Combine(Personal, "saved.sql"), "SELECT 2 FROM dual");
        Write(Path.Combine(general, "pulled.sql"), "SELECT 3 FROM dual");

        await library.RefreshAsync(Ct); // returns while the share still hangs: its read asked for once more

        await Wait.UntilAsync(() => library.For(null)[0].Scripts.Any(s => s.Path == "saved.sql"), "the personal folder is read while the share hangs");
        Assert.False(library.For(null)[1].Loaded);

        library.AfterRead = null;
        hanging.Set();
        await first;
        // The hanging read was done before "pulled.sql" was written: only the read asked for meanwhile can list it.
        Assert.Contains(library.For(null)[1].Scripts, s => s.Path == "pulled.sql");
    }

    /// <summary>
    /// Review WP-77, K9: only the first 16 KB are read for the header; a UTF-8 character cut at that edge made the whole
    /// head read as Windows-1252 ("AuftrÃ¤ge").
    /// </summary>
    /// <summary>
    /// Review 3.70.0, C4: a shared folder with a huge tree (a repository's node_modules) or thousands of scripts is listed
    /// only up to the limits, and says so (review fix M5 of WP-77 had no test).
    /// </summary>
    [Theory]
    [InlineData(2, 100, 2)] // the scripts
    [InlineData(100, 3, 3)] // the entries: the fourth of the root is not looked at, nor anything below
    public async Task A_folder_past_the_limits_is_listed_up_to_them_and_marked_as_cut(int files, int entries, int listed)
    {
        foreach (var name in new[] { "a.sql", "b.sql", "c.sql" })
        {
            Write(Path.Combine(Personal, name), "SELECT 1 FROM dual");
        }

        Write(Path.Combine(Personal, "sub", "d.sql"), "SELECT 1 FROM dual");
        var service = new AppSettingsService(new SettingsStore(_folder.Combine("settings.json")), AppSettings.Default);
        using var library = new ScriptLibrary(service, Personal, watch: false) { FileLimit = files, EntryLimit = entries };

        await library.RefreshAsync(Ct);
        var scan = library.For(null)[0];

        Assert.True(scan.Truncated);
        Assert.True(scan.Scripts.Count <= listed, $"{scan.Scripts.Count} listed");
    }

    [Fact]
    public async Task A_head_cut_inside_a_character_still_reads_as_utf8()
    {
        var head = "-- @description Aufträge\nSELECT ";
        var fill = ScriptLibrary.HeaderBytes - 1 - System.Text.Encoding.UTF8.GetByteCount(head); // "ä" spans the edge
        Write(Path.Combine(Personal, "big.sql"), head + new string('x', fill) + "ä;\n" + new string('y', 100));
        var (library, _) = Library();

        await library.RefreshAsync(Ct);

        Assert.Equal("Aufträge", Assert.Single(library.For(null)[0].Scripts).Description);
    }

    /// <summary>Review WP-77, K11: one folder that cannot be listed made the whole root "unreadable".</summary>
    [Fact]
    public async Task A_folder_that_cannot_be_listed_is_left_out()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("The denying ACL is a Windows one (as root under Linux every folder can be listed).");
            return;
        }

        Write(Path.Combine(Personal, "a.sql"), "SELECT 1 FROM dual");
        var closed = new DirectoryInfo(Path.Combine(Personal, "closed"));
        closed.Create();
        using (DenyListing(closed))
        {
            var (library, _) = Library();

            await library.RefreshAsync(Ct);

            var scan = Assert.Single(library.For(null));
            Assert.Null(scan.Error);
            Assert.Equal(["a.sql"], scan.Scripts.Select(s => s.Path));
        }
    }

    /// <summary>Denies the current user listing <paramref name="folder"/> until disposed (so the test folder can be removed).</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static IDisposable DenyListing(DirectoryInfo folder)
    {
        var rule = new System.Security.AccessControl.FileSystemAccessRule(
            System.Security.Principal.WindowsIdentity.GetCurrent().User!, System.Security.AccessControl.FileSystemRights.ListDirectory,
            System.Security.AccessControl.AccessControlType.Deny);
        var acl = folder.GetAccessControl();
        acl.AddAccessRule(rule);
        folder.SetAccessControl(acl);
        return new Undo(() =>
        {
            acl.RemoveAccessRule(rule);
            folder.SetAccessControl(acl);
        });
    }

    private sealed class Undo(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }

    public void Dispose()
    {
        _libraries.ForEach(l => l.Dispose());
        _folder.Dispose();
    }
}
