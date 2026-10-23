namespace FerretSharp.Core.ClrModel;

/// <summary>
/// What makes a project a test project (WP-40): the same frameworks by the names of their assemblies – what a built project
/// references (<see cref="IlUsageScanner"/>) – and of their packages – what a project file names, so known without a build
/// (<see cref="BuildOutputLocator.FindAssembly"/>). One list, so a built project and the same project not built agree.
/// </summary>
public static class TestFrameworks
{
    /// <summary>Assembly name prefixes: <c>xunit.core</c>, <c>nunit.framework</c>, <c>Microsoft.VisualStudio.TestPlatform…</c>.</summary>
    public static readonly IReadOnlyList<string> Assemblies =
        ["xunit", "nunit.framework", "Microsoft.VisualStudio.TestPlatform", "Microsoft.Testing.Platform", "MSTest", "TUnit"];

    /// <summary>Package name prefixes: <c>xunit.v3</c>, <c>NUnit3TestAdapter</c>, <c>MSTest.TestFramework</c>, the test SDK.</summary>
    public static readonly IReadOnlyList<string> Packages =
        ["xunit", "NUnit", "MSTest", "TUnit", "Microsoft.Testing.Platform", "Microsoft.NET.Test.Sdk"];

    public static bool IsTestAssembly(string assemblyName) => Assemblies.Any(f => assemblyName.StartsWith(f, StringComparison.OrdinalIgnoreCase));

    public static bool IsTestPackage(string packageName) => Packages.Any(f => packageName.StartsWith(f, StringComparison.OrdinalIgnoreCase));
}
