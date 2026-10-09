using FerretSharp.Core.Schema;
using FerretSharp.UI.Resources;

namespace FerretSharp.UI.State;

/// <summary>What the explorer lists (WP-28): tables, views and materialized views – or the schema's PL/SQL.</summary>
public enum ExplorerSection
{
    Tables,
    PlSql,
}

/// <summary>Filtering and letter groups of the explorer's lists, the same for both sections.</summary>
public static class ExplorerList
{
    /// <summary>Group key for names that do not start with A–Z (quoted identifiers like "1_Import" or "Übersicht").</summary>
    public const char Other = '#';

    /// <summary>True if the (trimmed) query is empty or one of the names contains it, ignoring case.</summary>
    public static bool Matches(string query, params ReadOnlySpan<string?> names)
    {
        var text = query.Trim();
        if (text.Length == 0)
        {
            return true;
        }

        foreach (var name in names)
        {
            if (name?.Contains(text, StringComparison.OrdinalIgnoreCase) == true)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The PL/SQL units the query finds: by the name shown and the real name.</summary>
    public static IEnumerable<PlSqlObjectSummary> Filter(IEnumerable<PlSqlObjectSummary> units, string query) =>
        units.Where(u => Matches(query, u.DisplayName, u.Name));

    public static char KeyOf(string name) => name.Length > 0 && name[0] is >= 'A' and <= 'Z' ? name[0] : Other;

    /// <summary>Items by first letter of the name shown, A–Z first and "#" (other) last; within a letter in list order.</summary>
    public static SortedDictionary<char, List<T>> Group<T>(IEnumerable<T> items, Func<T, string> displayName)
    {
        var result = new SortedDictionary<char, List<T>>(Comparer<char>.Create((a, b) => SortKey(a).CompareTo(SortKey(b))));
        foreach (var item in items)
        {
            var key = KeyOf(displayName(item));
            if (!result.TryGetValue(key, out var list))
            {
                result[key] = list = [];
            }

            list.Add(item);
        }

        return result;
    }

    private static int SortKey(char letter) => letter == Other ? int.MaxValue : letter;

    /// <summary>Short tag in the list, like VIEW/MVIEW for tables.</summary>
    public static string Tag(PlSqlKind kind) => kind switch
    {
        PlSqlKind.Package => "PKG",
        PlSqlKind.Procedure => "PROC",
        PlSqlKind.Function => "FUNC",
        _ => "TRG",
    };

    /// <summary>The kind as a word for tooltips and headers.</summary>
    public static string KindLabel(PlSqlKind kind) => kind switch
    {
        PlSqlKind.Package => SchemaViewText.Kind_Package,
        PlSqlKind.Procedure => SchemaViewText.Kind_Procedure,
        PlSqlKind.Function => SchemaViewText.Kind_Function,
        _ => SchemaViewText.Kind_Trigger,
    };
}

/// <summary>The schema-wide search in the PL/SQL source (WP-28): limits and how hits are shown.</summary>
public static class SourceSearch
{
    /// <summary>Shorter texts would match nearly every line.</summary>
    public const int MinLength = 3;

    /// <summary>At most this many lines; more means the search should be narrowed.</summary>
    public const int Limit = 500;

    /// <summary>Hits by unit and part, in the order they came (by name, then line).</summary>
    public static IReadOnlyList<IGrouping<(PlSqlRef Unit, PlSqlPart Part), SourceHit>> Group(IEnumerable<SourceHit> hits) =>
        hits.GroupBy(h => (h.Object, h.Part)).ToList();

    /// <summary>Column (from 1) where the text starts in the line, ignoring case; 1 if it is not found (e.g. split by a quoted name).</summary>
    public static int ColumnOf(string line, string text)
    {
        var index = line.IndexOf(text.Trim(), StringComparison.OrdinalIgnoreCase);
        return index < 0 ? 1 : index + 1;
    }
}
