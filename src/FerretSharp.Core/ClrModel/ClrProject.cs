namespace FerretSharp.Core.ClrModel;

/// <summary>The .NET project linked to a connection (WP-11): the project with the DbContext.</summary>
/// <param name="ProjectFile">Full path of the <c>.csproj</c>.</param>
/// <param name="Configuration">Build configuration whose output is read.</param>
/// <param name="ContextType">DbContext type (full or short name) if the project has several; null = its only one.</param>
public sealed record ClrProjectLink(string ProjectFile, string Configuration = "Debug", string? ContextType = null)
{
    public string ProjectName => Path.GetFileNameWithoutExtension(ProjectFile);
}
