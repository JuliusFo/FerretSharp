using FerretSharp.Core.ClrModel;
using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>WP-40: the folders the work clone dialog offers and what the status bar shows.</summary>
public sealed class WorkCloneChoicesTests : IDisposable
{
    private readonly TestFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private string Clone(string name, string branch, bool withProject = true)
    {
        var root = Directory.CreateDirectory(_folder.Combine(name)).FullName;
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        File.WriteAllText(Path.Combine(root, ".git", "HEAD"), $"ref: refs/heads/{branch}");
        if (withProject)
        {
            Directory.CreateDirectory(Path.Combine(root, "src", "Shop.Data"));
            File.WriteAllText(Path.Combine(root, "src", "Shop.Data", "Shop.Data.csproj"), "<Project />");
        }

        return root;
    }

    [Fact]
    public async Task The_active_clone_comes_first_with_branch_and_whether_it_fits_the_connection()
    {
        var link = new ClrProjectLink(Path.Combine(Clone("test-db", "test"), "src", "Shop.Data", "Shop.Data.csproj"));
        var feature = Clone("feature", "feature/a");
        var other = Clone("other-repo", "main", withProject: false);
        var clones = new WorkClones(_folder.Combine("work-clones.json"));
        await clones.SetActiveAsync("ERP", other, TestContext.Current.CancellationToken);
        await clones.SetActiveAsync("ERP", feature, TestContext.Current.CancellationToken);

        var choices = WorkCloneChoices.For(clones, "erp", link);

        Assert.Equal([new WorkCloneChoice(feature, "feature", "feature/a", WorkCloneFit.Fits, true), new WorkCloneChoice(other, "other-repo", "main", WorkCloneFit.ProjectMissing, false)], choices);
        Assert.Equal(choices[0], WorkCloneChoices.Active(clones, "ERP", link));
        Assert.Null(WorkCloneChoices.Active(clones, "Kasse", link));
        Assert.All(WorkCloneChoices.For(clones, "Kasse", null), c => Assert.False(c.IsActive));
    }

    [Fact]
    public void A_picked_solution_or_project_stands_for_its_working_copy()
    {
        var clone = Clone("work", "x");

        Assert.Equal(clone, WorkCloneChoices.RootOf(Path.Combine(clone, "src", "Shop.Data", "Shop.Data.csproj")));
        Assert.Equal(_folder.Combine("loose"), WorkCloneChoices.RootOf(_folder.Combine("loose", "Loose.sln")));
    }
}
