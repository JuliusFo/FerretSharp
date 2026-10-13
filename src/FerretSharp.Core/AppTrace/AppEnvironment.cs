using System.Text;

namespace FerretSharp.Core.AppTrace;

/// <summary>
/// The environment a traced app runs in, read from its command line (WP-33): the argument <c>environment</c> in the usual
/// spellings – <c>-environment=x</c>, <c>--environment=x</c>, <c>/environment=x</c>, or the value as the next argument
/// (<c>--environment x</c>), any letter case. Environment variables (<c>DOTNET_ENVIRONMENT</c>) cannot be read from
/// outside the process.
/// </summary>
public static class AppEnvironment
{
    public const string Key = "environment";

    private static readonly string[] Prefixes = ["--", "-", "/"];

    /// <summary>The environment name; null if the command line has none.</summary>
    public static string? FromCommandLine(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return null;
        }

        var args = Split(commandLine);
        for (var i = 1; i < args.Count; i++) // the first one is the program
        {
            if (NameOf(args[i]) is not { } name)
            {
                continue;
            }

            var separator = name.IndexOf('=');
            if (separator >= 0)
            {
                if (name[..separator].Equals(Key, StringComparison.OrdinalIgnoreCase) && name[(separator + 1)..].Trim() is { Length: > 0 } value)
                {
                    return value;
                }
            }
            else if (name.Equals(Key, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count && NameOf(args[i + 1]) is null && args[i + 1].Trim() is { Length: > 0 } next)
            {
                return next;
            }
        }

        return null;
    }

    /// <summary>The arguments of a Windows command line: split at white space outside double quotes, quotes removed.</summary>
    public static IReadOnlyList<string> Split(string commandLine)
    {
        var args = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        var any = false;
        foreach (var c in commandLine)
        {
            if (c == '"')
            {
                quoted = !quoted;
                any = true;
            }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (any)
                {
                    args.Add(current.ToString());
                    current.Clear();
                    any = false;
                }
            }
            else
            {
                current.Append(c);
                any = true;
            }
        }

        if (any)
        {
            args.Add(current.ToString());
        }

        return args;
    }

    /// <summary>The argument without its switch prefix; null if it is no switch.</summary>
    private static string? NameOf(string arg) =>
        Prefixes.FirstOrDefault(p => arg.Length > p.Length && arg.StartsWith(p, StringComparison.Ordinal)) is { } prefix ? arg[prefix.Length..] : null;
}
