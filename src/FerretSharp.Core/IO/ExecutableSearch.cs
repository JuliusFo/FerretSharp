namespace FerretSharp.Core.IO;

/// <summary>Finding a program as the shell would (review 3.44.0, B8: dotnet and VS Code each searched PATH their own way).</summary>
public static class ExecutableSearch
{
    /// <summary>The first <paramref name="fileName"/> in a folder of PATH, then in <paramref name="more"/>; null if none.</summary>
    /// <param name="more">Folders after PATH, e.g. where an installer puts the program without adding it to PATH.</param>
    public static string? FindOnPath(string fileName, IEnumerable<string>? more = null) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Concat(more ?? [])
            .Select(d => d.Trim().Trim('"'))
            .Where(d => d.Length > 0)
            .Select(d => Path.Combine(d, fileName))
            .FirstOrDefault(File.Exists);
}
