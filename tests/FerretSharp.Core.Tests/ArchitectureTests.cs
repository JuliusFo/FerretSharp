using FerretSharp.Core;

namespace FerretSharp.Core.Tests;

public class ArchitectureTests
{
    private static readonly string[] ForbiddenAssemblies =
    [
        "PresentationCore",
        "PresentationFramework",
        "WindowsBase",
        "System.Windows.Forms",
    ];

    [Fact]
    public void Core_does_not_reference_ui_assemblies()
    {
        var referenced = typeof(AppPaths).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name)
            .ToList();

        Assert.DoesNotContain(referenced, name => ForbiddenAssemblies.Contains(name));
    }
}
