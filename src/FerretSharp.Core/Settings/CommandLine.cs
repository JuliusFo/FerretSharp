namespace FerretSharp.Core.Settings;

/// <summary>
/// The options FerretSharp takes on its command line (<c>--name=value</c>, for tests, screenshots and separate profiles)
/// and one way to read them (R5b, review 3.31.0 B12: five copies; the language took the last occurrence, the others the
/// first).
/// </summary>
public static class CommandLine
{
    /// <summary>The data folder (connections, workspaces, settings, logs).</summary>
    public const string DataDir = "--data-dir=";

    /// <summary>Where the import looks for DBeaver and SQL Developer instead of <c>%APPDATA%</c>.</summary>
    public const string ImportAppData = "--import-appdata=";

    /// <summary><c>dark</c> or <c>light</c> for this session.</summary>
    public const string Theme = "--theme=";

    /// <summary><c>en</c> or <c>de</c> for this session.</summary>
    public const string Language = "--lang=";

    /// <summary>A folder with releases to update from instead of GitHub.</summary>
    public const string UpdateSource = "--update-source=";

    /// <summary>The options that name a path: an update starts the new version with them as full paths.</summary>
    public static readonly IReadOnlyList<string> PathOptions = [DataDir, ImportAppData, UpdateSource];

    /// <summary>The value of the first <paramref name="option"/> (the case of its name ignored); null without it.</summary>
    public static string? ValueOf(IEnumerable<string> args, string option) =>
        args.FirstOrDefault(a => a.StartsWith(option, StringComparison.OrdinalIgnoreCase))?[option.Length..];
}
