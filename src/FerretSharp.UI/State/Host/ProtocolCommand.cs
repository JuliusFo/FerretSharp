namespace FerretSharp.UI.State;

/// <summary>
/// The command line Windows runs for a <c>ferretsharp://</c> link (WP-37, ADR 0026), and whether it is this FerretSharp's –
/// what decides if an uninstall removes the registration. R6b (review 3.40.0, C11): out of the app's registry code,
/// which has no tests.
/// </summary>
public static class ProtocolCommand
{
    /// <summary><c>"C:\…\FerretSharp.exe" "%1"</c>: the link as the one argument.</summary>
    public static string For(string program) => $"\"{program}\" \"%1\"";

    /// <summary>The program of a command line, quoted or not; null for none.</summary>
    public static string? ProgramOf(string? command)
    {
        var text = command?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            return end > 1 ? text[1..end] : null;
        }

        var space = text.IndexOf(' ', StringComparison.Ordinal);
        return space < 0 ? text : text[..space];
    }

    /// <summary>The same program file (Windows paths ignore letter case; <c>..</c> and the like resolved).</summary>
    public static bool SameProgram(string? registered, string program) =>
        registered is not null && string.Equals(Path.GetFullPath(registered), Path.GetFullPath(program), StringComparison.OrdinalIgnoreCase);
}
