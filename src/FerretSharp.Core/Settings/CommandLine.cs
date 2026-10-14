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

    /// <summary>
    /// A restart (after the graphics output failed): the process id of the instance it replaces. The new one waits until that
    /// has ended – it holds the data folder's instance lock until then.
    /// </summary>
    public const string WaitFor = "--wait-for=";

    /// <summary>The options that name a path: an update starts the new version with them as full paths.</summary>
    public static readonly IReadOnlyList<string> PathOptions = [DataDir, ImportAppData, UpdateSource];

    /// <summary>
    /// The arguments to start FerretSharp again as it was started: paths as full paths (the new process may start in another
    /// working folder, and a relative <c>--data-dir</c> would point at an empty one – review 3.31.0, A44), and the process to
    /// wait for (<see cref="WaitFor"/>) instead of an earlier one.
    /// </summary>
    public static IReadOnlyList<string> Restart(IEnumerable<string> args, int? waitFor = null) =>
    [
        .. args.Where(a => !a.StartsWith(WaitFor, StringComparison.OrdinalIgnoreCase)).Select(WithFullPath),
        .. waitFor is { } id ? [WaitFor + id.ToString(System.Globalization.CultureInfo.InvariantCulture)] : Array.Empty<string>(),
    ];

    private static string WithFullPath(string argument) =>
        PathOptions.FirstOrDefault(p => argument.StartsWith(p, StringComparison.OrdinalIgnoreCase)) is { } prefix
        && argument.Length > prefix.Length
            ? prefix + Path.GetFullPath(argument[prefix.Length..])
            : argument;

    /// <summary>The value of the first <paramref name="option"/> (the case of its name ignored); null without it.</summary>
    public static string? ValueOf(IEnumerable<string> args, string option) =>
        args.FirstOrDefault(a => a.StartsWith(option, StringComparison.OrdinalIgnoreCase))?[option.Length..];
}
