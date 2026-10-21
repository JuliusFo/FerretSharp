namespace FerretSharp.UI.State;

/// <summary>How VS Code is started for a file at a line (WP-40): the program and its arguments, or why not.</summary>
public sealed record VsCodeStart(string? Program, IReadOnlyList<string> Arguments, string? Refusal);

/// <summary>
/// Starts VS Code without <c>cmd.exe</c> where possible (review of WP-40): <c>code.cmd</c> is a batch file, and cmd parses
/// the command line – an unquoted <c>&amp;</c>, <c>^</c> or <c>%</c> in a path (<c>C:\Repos\R&amp;D\…</c>) splits or expands it.
/// <c>Code.exe</c> next to the <c>bin</c> folder takes the same <c>-g file:line</c>.
/// </summary>
public static class VsCodeCommand
{
    private static readonly char[] CmdSpecial = ['&', '|', '<', '>', '^', '%', '"', '!'];

    /// <param name="codeCmd">The <c>code.cmd</c> found (PATH or the user installation); null if none.</param>
    /// <param name="exists">File check (tests).</param>
    public static VsCodeStart For(string? codeCmd, string file, int? line, Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        if (codeCmd is null)
        {
            return new VsCodeStart(null, [], Resources.ModelViewText.Usage_NoVsCode);
        }

        string[] arguments = ["-g", line is { } at ? $"{file}:{at}" : file];
        var bin = Path.GetDirectoryName(codeCmd);
        var exe = bin is null ? null : Path.Combine(Path.GetDirectoryName(bin) ?? bin, "Code.exe");
        if (exe is not null && exists(exe))
        {
            return new VsCodeStart(exe, arguments, null);
        }

        return file.IndexOfAny(CmdSpecial) >= 0
            ? new VsCodeStart(null, [], Resources.ModelViewText.Usage_VsCodePath)
            : new VsCodeStart(codeCmd, arguments, null);
    }
}
