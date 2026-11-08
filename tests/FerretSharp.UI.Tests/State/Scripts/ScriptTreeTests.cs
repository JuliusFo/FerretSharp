using FerretSharp.Core.Query;
using FerretSharp.Core.Scripts;
using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>WP-77: the script tree of the explorer and the search of the scripts panel.</summary>
public sealed class ScriptTreeTests
{
    private static readonly ScriptRoot Personal = new(ScriptRootKind.Personal, "/p");
    private static readonly ScriptRoot Erp = new(ScriptRootKind.Group, "/erp", "ERP");

    private static ScriptEntry Script(ScriptRoot root, string path, string? description = null) =>
        new(root, path, description, [], [], DateTime.UnixEpoch, 1);

    private static readonly IReadOnlyList<ScriptScan> Scans =
    [
        new(Personal, ["Empty", "Locks", "Locks/Oracle"], [Script(Personal, "Locks/Oracle/Sessions.sql", "who holds what"), Script(Personal, "Mine.sql")], null, false),
        new(Erp, ["Orders"], [Script(Erp, "Orders/Open.sql", "open orders of a customer")], null, false),
    ];

    private static string Shape(IEnumerable<ScriptRow> rows) =>
        string.Join(" | ", rows.Select(r => new string('.', r.Depth) + r.Label + (r.Script is null && !r.Expanded ? "+" : "")));

    [Fact]
    public void Each_folder_lists_its_folders_then_its_scripts_and_a_collapsed_one_hides_its_content()
    {
        Assert.Equal(
            "Persönlich | .Empty | .Locks | ..Oracle | ...Sessions | .Mine | Gruppe ERP | .Orders | ..Open",
            Shape(ScriptTree.Rows(Scans, "", new HashSet<string>())));

        var collapsed = new HashSet<string> { ScriptTree.KeyOf(Personal, "Locks"), ScriptTree.KeyOf(Erp, "") };
        Assert.Equal("Persönlich | .Empty | .Locks+ | .Mine | Gruppe ERP+", Shape(ScriptTree.Rows(Scans, null, collapsed)));
    }

    [Fact]
    public void A_search_shows_the_scripts_it_finds_with_the_folders_leading_there_all_open()
    {
        var collapsed = new HashSet<string> { ScriptTree.KeyOf(Personal, "Locks") };

        Assert.Equal("Persönlich | .Locks | ..Oracle | ...Sessions", Shape(ScriptTree.Rows(Scans, "sess", collapsed)));
        Assert.Equal(1, ScriptTree.Count(Scans, "sess"));
        Assert.Equal(0, ScriptTree.Count(Scans, "customer")); // the explorer finds by name only
    }

    [Fact]
    public void The_panel_finds_by_name_description_and_folder()
    {
        Assert.Equal(["Orders/Open.sql"], ScriptTree.Search(Scans, "customer").Select(s => s.Path));
        Assert.Equal(["Locks/Oracle/Sessions.sql"], ScriptTree.Search(Scans, "oracle who").Select(s => s.Path));
        Assert.Equal(3, ScriptTree.Search(Scans, "").Count);
    }
}
