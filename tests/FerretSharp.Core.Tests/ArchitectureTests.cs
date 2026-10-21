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

    /// <summary>Where SQL and DDL text may be written (CLAUDE.md §4): the query builders, the Oracle layer, the DDL texts.</summary>
    private static readonly string[] SqlFolders = ["src/FerretSharp.Core/Query/", "src/FerretSharp.Core/Oracle/", "src/FerretSharp.Core/Ddl/"];

    /// <summary>Statement text outside them that only shows an example (allowed: "display of a generated statement").</summary>
    private static readonly (string File, string Text)[] SqlShownOnly =
    [
        ("src/FerretSharp.UI/Components/Sql/SqlView.razor", "Placeholder="),
        ("src/FerretSharp.UI/State/Settings/SettingsCatalog.cs", "LockWaitClause = "),
    ];

    /// <summary>A string that starts a statement, as written by hand: keywords upper case, as everywhere in FerretSharp.</summary>
    private static readonly System.Text.RegularExpressions.Regex StatementText = new(
        "\"[$@]?\\s*(SELECT |INSERT INTO |UPDATE [A-Za-z\"]|DELETE FROM |MERGE INTO |ALTER (TABLE|USER|SESSION) |CREATE (OR REPLACE )?(TABLE|INDEX|VIEW|TRIGGER|SEQUENCE) |DROP (TABLE|INDEX|VIEW|TRIGGER) |COMMENT ON )");

    /// <summary>
    /// Review 3.44.0, C11: only the reference to WPF had a tripwire. SQL in a component or a state class would pass the
    /// guards (it is a query), but bypass the builders: binds, quoting, the deterministic order.
    /// </summary>
    [Fact]
    public void Sql_text_is_written_only_in_query_oracle_and_ddl()
    {
        var root = FerretSharp.Testing.Repository.Root;
        var found = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".razor", StringComparison.Ordinal))
            .Select(f => (File: f, Relative: Path.GetRelativePath(root, f).Replace('\\', '/')))
            .Where(f => !f.Relative.Contains("/obj/", StringComparison.Ordinal) && !f.Relative.Contains("/bin/", StringComparison.Ordinal)
                        && !SqlFolders.Any(folder => f.Relative.StartsWith(folder, StringComparison.Ordinal)))
            .SelectMany(f => File.ReadLines(f.File)
                .Select(line => line.Trim())
                .Where(line => !line.StartsWith("//", StringComparison.Ordinal) && !line.StartsWith('*') && !line.StartsWith("@*", StringComparison.Ordinal))
                .Where(line => StatementText.IsMatch(line))
                .Select(line => f.Relative + ": " + line))
            .Where(hit => !SqlShownOnly.Any(allowed => hit.StartsWith(allowed.File + ": ", StringComparison.Ordinal) && hit.Contains(allowed.Text, StringComparison.Ordinal)))
            .ToList();

        Assert.Empty(found);
    }
}
