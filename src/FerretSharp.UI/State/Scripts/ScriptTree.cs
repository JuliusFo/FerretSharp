using FerretSharp.Core.Query;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Scripts;
using FerretSharp.UI.Resources;

namespace FerretSharp.UI.State;

/// <summary>A line of the script tree in the explorer (WP-77): a folder of the settings, a folder below it, or a script.</summary>
/// <param name="Key">Identifies the line among all (collapsing, Blazor keys).</param>
/// <param name="Folder">The folder below the root of a folder line ("" for the root line); null for a script.</param>
/// <param name="Script">The script of a script line; null otherwise.</param>
/// <param name="Expanded">For a root or folder line: its content is listed below it.</param>
public sealed record ScriptRow(ScriptScan Scan, string Key, int Depth, string Label, string? Folder, ScriptEntry? Script, bool Expanded)
{
    public bool IsRoot => Folder is { Length: 0 };
}

/// <summary>The script tree of the explorer and the search of the scripts panel (WP-77).</summary>
public static class ScriptTree
{
    /// <summary>The label of a folder of the settings: "Personal", "Group ERP", "General".</summary>
    public static string RootLabel(ScriptRoot root) => root.Kind switch
    {
        ScriptRootKind.Group => TextFormat.Format(ScriptViewText.Root_Group, root.Group),
        ScriptRootKind.General => ScriptViewText.Root_General,
        _ => ScriptViewText.Root_Personal,
    };

    /// <summary>The explorer's search: by name, as in the other sections.</summary>
    public static bool NameMatches(ScriptEntry script, string? query) => new TextSearch(query).Matches(script.Name);

    /// <summary>The scripts the explorer's search finds in all folders ("3 hits in Scripts").</summary>
    public static int Count(IEnumerable<ScriptScan> scans, string? query) =>
        scans.Sum(s => s.Scripts.Count(e => NameMatches(e, query)));

    /// <summary>
    /// The scripts panel's search (decision of the user): every word in the name, the description or the folder; all
    /// scripts without a query, in the order of the folders of the settings.
    /// </summary>
    public static IReadOnlyList<ScriptEntry> Search(IEnumerable<ScriptScan> scans, string? query)
    {
        var search = new TextSearch(query);
        return [.. scans.SelectMany(s => s.Scripts).Where(e => search.Matches(e.Name, e.Description, e.Folder))];
    }

    /// <summary>
    /// The lines of the tree: each folder of the settings, below it its folders (also empty ones) and then its scripts, by
    /// name. With a query only the scripts it finds and the folders that lead to them, all expanded.
    /// </summary>
    /// <param name="collapsed">Keys of the lines the user collapsed.</param>
    public static IReadOnlyList<ScriptRow> Rows(IReadOnlyList<ScriptScan> scans, string? query, IReadOnlySet<string> collapsed)
    {
        ArgumentNullException.ThrowIfNull(scans);
        ArgumentNullException.ThrowIfNull(collapsed);
        var searching = new TextSearch(query).IsActive;
        var rows = new List<ScriptRow>();
        foreach (var scan in scans)
        {
            var rootKey = KeyOf(scan.Root, "");
            var scripts = searching ? scan.Scripts.Where(e => NameMatches(e, query)).ToList() : scan.Scripts;
            if (searching && scripts.Count == 0)
            {
                continue;
            }

            var expanded = searching || !collapsed.Contains(rootKey);
            rows.Add(new ScriptRow(scan, rootKey, 0, RootLabel(scan.Root), "", null, expanded));
            if (!expanded)
            {
                continue;
            }

            var folders = searching
                ? scripts.SelectMany(e => Ancestors(e.Folder)).ToHashSet(StringComparer.Ordinal)
                : scan.Folders.ToHashSet(StringComparer.Ordinal);
            Emit("", 1);

            void Emit(string folder, int depth)
            {
                foreach (var child in folders.Where(f => Parent(f) == folder)
                             .Order(StringComparer.CurrentCultureIgnoreCase))
                {
                    var key = KeyOf(scan.Root, child);
                    var open = searching || !collapsed.Contains(key);
                    rows.Add(new ScriptRow(scan, key, depth, child[(child.LastIndexOf('/') + 1)..], child, null, open));
                    if (open)
                    {
                        Emit(child, depth + 1);
                    }
                }

                foreach (var script in scripts.Where(e => e.Folder == folder))
                {
                    rows.Add(new ScriptRow(scan, KeyOf(scan.Root, script.Path), depth, script.Name, null, script, false));
                }
            }
        }

        return rows;
    }

    /// <summary>The key of a folder or script of a root.</summary>
    public static string KeyOf(ScriptRoot root, string path) => $"{root.Kind}|{root.Group}|{path}";

    private static string Parent(string folder) => folder.LastIndexOf('/') is var i and >= 0 ? folder[..i] : "";

    /// <summary>The folder and all above it ("a/b" → "a", "a/b"); none for the root.</summary>
    private static IEnumerable<string> Ancestors(string folder)
    {
        for (var i = 0; i < folder.Length; i++)
        {
            if (folder[i] == '/')
            {
                yield return folder[..i];
            }
        }

        if (folder.Length > 0)
        {
            yield return folder;
        }
    }
}
