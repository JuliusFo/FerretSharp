namespace FerretSharp.UI.State;

/// <summary>Which files "open with the default program" hands to the shell (review 3.44.0, A29).</summary>
public static class SourceFiles
{
    /// <summary>The source files a PDB names: C#, Razor and their generated parts, VB, F#, XAML.</summary>
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".cs", ".razor", ".cshtml", ".vb", ".fs", ".xaml" };

    /// <summary>
    /// A source file, never a program: the path comes from the PDB of a build, and the shell would run a <c>.bat</c>,
    /// <c>.lnk</c> or <c>.url</c> a crafted <c>#line</c> points to.
    /// </summary>
    public static bool MayOpenWithShell(string path) =>
        !path.StartsWith(@"\\", StringComparison.Ordinal) && Extensions.Contains(Path.GetExtension(path));
}
