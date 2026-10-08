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
}
