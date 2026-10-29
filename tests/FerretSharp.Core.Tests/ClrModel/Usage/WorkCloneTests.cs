using FerretSharp.Core.ClrModel;
using Microsoft.Extensions.Time.Testing;

namespace FerretSharp.Core.Tests.ClrModel.Usage;

/// <summary>The work clone: Git folders and branches, the state per group, and mapping paths of the linked clone onto it.</summary>
public sealed class WorkCloneTests : IDisposable
{
    private readonly TestFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private string Write(string relative, string content = "")
    {
        var file = _folder.Combine(relative.Split('/'));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, content);
        return file;
    }

    /// <summary>A clone with a .git folder on <paramref name="branch"/> and the shop's data project.</summary>
    private string Clone(string name, string branch)
    {
        Write($"{name}/.git/HEAD", $"ref: refs/heads/{branch}\n");
        Write($"{name}/src/Shop.Data/Shop.Data.csproj", "<Project />");
        Write($"{name}/src/Shop.Data/Customer.cs", "namespace Shop; public class Customer { }");
        return _folder.Combine(name);
    }

    [Fact]
    public void The_branch_comes_from_head_also_in_a_worktree_or_detached()
    {
        var clone = Clone("dev-db", "dev");
        Write("worktrees-meta/feature/HEAD", "ref: refs/heads/feature/xyz");
        Write("feature/.git", "gitdir: ../worktrees-meta/feature\n");
        Write("detached/.git/HEAD", "0123456789abcdef0123456789abcdef01234567");

        Assert.Equal("dev", GitFolder.Branch(clone));
        Assert.Equal("feature/xyz", GitFolder.Branch(_folder.Combine("feature")));
        Assert.Equal("0123456", GitFolder.Branch(_folder.Combine("detached")));
        Assert.Null(GitFolder.Branch(Directory.CreateDirectory(_folder.Combine("plain")).FullName));
        Assert.Equal(clone, GitFolder.FindRoot(Path.Combine(clone, "src", "Shop.Data", "Customer.cs")));

        // Review 3.44.0, A31: a share the working copy is not on is not followed (a sign-in to a foreign host every few seconds).
        Write("unpacked/.git", @"gitdir: \\fremd.invalid\share\meta" + "\n");
        Assert.Null(GitFolder.Branch(_folder.Combine("unpacked")));
    }

    [Fact]
    public void A_work_clone_fits_when_the_linked_project_is_there_under_the_same_relative_path()
    {
        var linked = Path.Combine(Clone("dev-db", "dev"), "src", "Shop.Data", "Shop.Data.csproj");
        var work = Clone("work", "feature/x");
        Write("other/.git/HEAD", "ref: refs/heads/main");

        Assert.Equal(WorkCloneFit.Fits, WorkCloneMapping.Check(linked, work));
        Assert.Equal(WorkCloneFit.ProjectMissing, WorkCloneMapping.Check(linked, _folder.Combine("other")));
        Assert.Equal(WorkCloneFit.Missing, WorkCloneMapping.Check(linked, _folder.Combine("gone")));
        Assert.Equal(WorkCloneFit.LinkedNotInRepository, WorkCloneMapping.Check(Write("loose/Loose.csproj"), work));
    }

    [Fact]
    public void A_source_file_opens_in_the_work_clone_then_by_type_then_in_the_linked_clone()
    {
        var linkedRoot = Clone("dev-db", "dev");
        var linked = Path.Combine(linkedRoot, "src", "Shop.Data", "Shop.Data.csproj");
        var work = Clone("work", "feature/x");
        var service = Write("dev-db/src/Shop.Data/Services/CustomerService.cs", "class CustomerService { }");
        var moved = Write("work/src/Shop.Data/Moved/CustomerService.cs", "namespace Shop; internal sealed class CustomerService { }");
        Write("work/src/Shop.Data/bin/Debug/CustomerService.cs", "class CustomerService { }");

        Assert.Equal(new SourceLocation(Path.Combine(work, "src", "Shop.Data", "Customer.cs"), true),
            WorkCloneMapping.Resolve(Path.Combine(linkedRoot, "src", "Shop.Data", "Customer.cs"), linked, work, "Shop.Customer", TestContext.Current.CancellationToken));
        Assert.Equal(new SourceLocation(moved, true, ByType: true), WorkCloneMapping.Resolve(service, linked, work, "Shop.Services.CustomerService", TestContext.Current.CancellationToken));
        Assert.Equal(new SourceLocation(service, false), WorkCloneMapping.Resolve(service, linked, work, "Shop.Services.Nowhere", TestContext.Current.CancellationToken));
        Assert.Equal(new SourceLocation(service, false), WorkCloneMapping.Resolve(service, linked, null, "Shop.Services.CustomerService", TestContext.Current.CancellationToken));
        Assert.Equal(new SourceLocation(service, false), WorkCloneMapping.Resolve(service, linked, _folder.Combine("gone"), null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void The_solution_maps_onto_the_work_clone_when_it_is_there()
    {
        var linkedRoot = Clone("dev-db", "dev");
        var linked = Path.Combine(linkedRoot, "src", "Shop.Data", "Shop.Data.csproj");
        var work = Clone("work", "feature/x");
        var solution = Write("dev-db/Server.slnx", "<Solution />");
        var other = Write("dev-db/Client.slnx", "<Solution />");
        Write("work/Server.slnx", "<Solution />");

        Assert.Equal(Path.Combine(work, "Server.slnx"), WorkCloneMapping.MapSolution(solution, linked, work));
        Assert.Equal(other, WorkCloneMapping.MapSolution(other, linked, work));
        Assert.Equal(solution, WorkCloneMapping.MapSolution(solution, linked, null));
    }

    [Fact]
    public void A_deterministic_build_path_is_relative_to_the_repository_root()
    {
        var linkedRoot = Clone("dev-db", "dev");
        var linked = Path.Combine(linkedRoot, "src", "Shop.Data", "Shop.Data.csproj");
        var work = Clone("work", "feature/x");

        Assert.Equal(new SourceLocation(Path.Combine(work, "src", "Shop.Data", "Customer.cs"), true),
            WorkCloneMapping.Resolve("/_/src/Shop.Data/Customer.cs", linked, work, null, TestContext.Current.CancellationToken));
        Assert.Equal(new SourceLocation(Path.Combine(linkedRoot, "src", "Shop.Data", "Customer.cs"), false),
            WorkCloneMapping.Resolve("/_/src/Shop.Data/Customer.cs", linked, null, null, TestContext.Current.CancellationToken));

        // Review 3.44.0, A29: out of the repository through the deterministic path, as the absolute one refuses it.
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(linkedRoot)!, "x.cmd"), "");
        Assert.Null(WorkCloneMapping.Resolve("/_/../x.cmd", linked, null, null, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Review 3.54.0, A9: an absolute path from the PDB outside the repository was opened as it was – a
    /// <c>#line 1 "\\host\share\x.cs"</c> signed in on that host (not tried here: the test would do just that).
    /// </summary>
    [Fact]
    public void An_absolute_path_outside_the_repository_is_not_opened()
    {
        var linkedRoot = Clone("dev-db", "dev");
        var linked = Path.Combine(linkedRoot, "src", "Shop.Data", "Shop.Data.csproj");
        var outside = Write("elsewhere/Customer.cs", "class Customer { }");

        Assert.Null(WorkCloneMapping.Resolve(outside, linked, null, null, TestContext.Current.CancellationToken));
        Assert.Equal(new SourceLocation(outside, false), WorkCloneMapping.Resolve(outside, Write("loose/Loose.csproj"), null, null, TestContext.Current.CancellationToken));

        // Review of R8: the same through the deterministic path – "/_/" with a root after it (here a local one, a share would
        // be tried), which Path.Combine returned as it was.
        var deterministic = "/_/" + outside.Replace('\\', '/');
        Assert.Null(WorkCloneMapping.Resolve(deterministic, linked, null, null, TestContext.Current.CancellationToken));
        Assert.Null(WorkCloneMapping.Resolve(deterministic, linked, Clone("work", "feature/x"), null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void A_nested_or_generic_type_is_found_by_its_simple_name_files_named_after_it_first()
    {
        var root = Directory.CreateDirectory(_folder.Combine("repo")).FullName;
        Write("repo/a/Mentions.cs", "// class Line is mentioned here\nclass LineHelper { }");
        var named = Write("repo/z/Repository.cs", "public class Repository<T> { }");
        var nested = Write("repo/m/Order.cs", "class Order { public class Line { } }");

        Assert.Equal(named, WorkCloneMapping.FindTypeFile(root, "Shop.Repository`1", TestContext.Current.CancellationToken));
        Assert.Equal(nested, WorkCloneMapping.FindTypeFile(root, "Shop.Order+Line", TestContext.Current.CancellationToken));
        Assert.Null(WorkCloneMapping.FindTypeFile(root, "Shop.Missing", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Each_group_has_its_own_active_clone_and_choices_feed_the_recent_list()
    {
        var file = _folder.Combine("work-clones.json");
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-20T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var erp = Clone("erp-work", "feature/a");
        var kasse = Clone("kasse-work", "feature/b");
        var ungrouped = Clone("solo", "main");
        var clones = new WorkClones(file, time);
        var changed = 0;
        clones.Changed += () => changed++;

        await clones.SetActiveAsync("ERP", erp, TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromMinutes(1));
        await clones.SetActiveAsync(" kasse ", kasse, TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromMinutes(1));
        await clones.SetActiveAsync(null, ungrouped, TestContext.Current.CancellationToken);

        var loaded = new WorkClones(file);
        await loaded.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(erp, loaded.ActiveFor("erp"));
        Assert.Equal(kasse, loaded.ActiveFor("Kasse"));
        Assert.Equal(ungrouped, loaded.ActiveFor("  "));
        Assert.Null(loaded.ActiveFor("Other"));
        Assert.Equal([ungrouped, kasse, erp], loaded.Recent);
        Assert.Equal(3, changed);

        // Back to the linked clone for one group; the folder stays in the recent list.
        await loaded.SetActiveAsync("ERP", null, TestContext.Current.CancellationToken);
        Assert.Null(loaded.ActiveFor("ERP"));
        Assert.Contains(erp, loaded.Recent);
    }

    [Fact]
    public async Task Missing_folders_drop_out_and_a_newer_file_is_kept()
    {
        var file = _folder.Combine("work-clones.json");
        var gone = _folder.Combine("gone");
        File.WriteAllText(file, $$"""{ "version": 1, "active": [null, { "group": "ERP", "path": {{System.Text.Json.JsonSerializer.Serialize(gone)}} }], "recent": [{ "path": {{System.Text.Json.JsonSerializer.Serialize(gone)}}, "lastUsed": "2026-10-20T10:00:00Z" }, null] }""");
        var clones = new WorkClones(file);
        await clones.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(gone, clones.ActiveFor("ERP"));
        Assert.Empty(clones.Recent);

        const string newer = """{ "version": 99, "somethingNew": true }""";
        File.WriteAllText(file, newer);
        await clones.LoadAsync(TestContext.Current.CancellationToken);
        await clones.SetActiveAsync("ERP", Clone("work", "x"), TestContext.Current.CancellationToken);
        Assert.Equal(newer, File.ReadAllText(file));
        Assert.Equal(_folder.Combine("work"), clones.ActiveFor("ERP"));
    }

    /// <summary>Review 3.44.0, A22: a file held by a backup or a sync ended in "unexpected error"; the choice applies all the same.</summary>
    [Fact]
    public async Task A_locked_file_keeps_the_choice_in_memory_and_says_so()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows locks a file against replacing it.");
        var file = _folder.Combine("work-clones.json");
        var work = Clone("work", "x");
        var clones = new WorkClones(file);
        await clones.SetActiveAsync("ERP", Clone("first", "y"), TestContext.Current.CancellationToken);

        bool saved;
        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            saved = await clones.SetActiveAsync("ERP", work, TestContext.Current.CancellationToken);
        }

        Assert.False(saved);
        Assert.Equal(work, clones.ActiveFor("ERP"));
        Assert.True(await clones.SetActiveAsync("ERP", work, TestContext.Current.CancellationToken)); // free again: saved
    }

    /// <summary>WP-66: renaming a group takes its work clone along; merged into a group with one, that one stays.</summary>
    [Fact]
    public async Task A_renamed_group_keeps_its_work_clone()
    {
        var file = _folder.Combine("work-clones.json");
        var erp = Clone("erp-work", "feature/a");
        var kasse = Clone("kasse-work", "feature/b");
        var clones = new WorkClones(file);
        await clones.SetActiveAsync("ERP", erp, TestContext.Current.CancellationToken);
        await clones.SetActiveAsync("Kasse", kasse, TestContext.Current.CancellationToken);

        Assert.True(await clones.RenameGroupAsync("erp", "Warenwirtschaft", TestContext.Current.CancellationToken));
        var loaded = new WorkClones(file);
        await loaded.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(erp, loaded.ActiveFor("Warenwirtschaft"));
        Assert.Null(loaded.ActiveFor("ERP"));

        await loaded.RenameGroupAsync("Warenwirtschaft", "Kasse", TestContext.Current.CancellationToken);
        Assert.Equal(kasse, loaded.ActiveFor("Kasse"));
        Assert.Null(loaded.ActiveFor("Warenwirtschaft"));

        Assert.True(await loaded.RenameGroupAsync("Nothing", "Else", TestContext.Current.CancellationToken));
    }
}
