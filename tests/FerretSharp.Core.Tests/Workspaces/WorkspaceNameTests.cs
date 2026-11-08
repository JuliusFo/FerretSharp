using FerretSharp.Core.Workspaces;

namespace FerretSharp.Core.Tests.Workspaces;

public sealed class WorkspaceNameTests
{
    [Theory]
    [InlineData(new string[0], "Workspace 1")]
    [InlineData(new[] { "Workspace 1", "Workspace 2" }, "Workspace 3")]
    [InlineData(new[] { "Bug 3711", "Workspace 2" }, "Workspace 1")] // "Workspace 1" was renamed: its number is free again
    [InlineData(new[] { "workspace 1" }, "Workspace 2")] // ignoring case
    public void First_free_name_takes_the_smallest_number(string[] taken, string expected) =>
        Assert.Equal(expected, Workspace.FirstFreeName("Workspace ", taken));

    /// <summary>Review WP-77, K5: a script's name longer than a tab title may be was set unchecked or not at all.</summary>
    [Fact]
    public void A_long_name_is_cut_to_fit_with_an_ellipsis()
    {
        var name = new string('a', 60);

        var fitted = Workspace.FitName(name);

        Assert.Equal(Workspace.MaxNameLength, fitted.Length);
        Assert.EndsWith("…", fitted, StringComparison.Ordinal);
        Assert.Equal(fitted, Workspace.NormalizeName(fitted));
        Assert.Equal("Short", Workspace.FitName(" Short "));
        Assert.False(char.IsHighSurrogate(Workspace.FitName(new string('a', 38) + "😀😀")[^2])); // never inside a pair
    }
}
