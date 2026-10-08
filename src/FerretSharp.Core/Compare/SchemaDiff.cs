using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Compare;

/// <summary>
/// Compares N schema snapshots (WP-20) into a matrix: one row per object, its columns, constraints and indexes as
/// children, one cell per side. Pure logic on snapshots; the definitions compared are built by
/// <see cref="CompareDefinitions"/>.
/// </summary>
/// <remarks>
/// Matching:
/// <list type="bullet">
/// <item>Objects, columns and user-named constraints and indexes by exact name. A side that has the name only in another
/// letter case is <see cref="CellState.OtherCase"/> in that row. If one side has several names differing only in case,
/// pairing them up would be guessing: then every spelling is its own row and only exact names match.</item>
/// <item>The primary key by type: one per table, whatever its name.</item>
/// <item>Constraints and indexes whose name Oracle generated (<c>SYS_C…</c>) by content (<see cref="CompareDefinitions.ConstraintSignature"/>,
/// <see cref="CompareDefinitions.IndexSignature"/>). A generated one with the same content as a user-named one on
/// another side joins the named row (the side may lack the name because the DDL there had none): one row that shows
/// the other name in <see cref="CompareCell.Name"/> rather than a missing and an extra row. Names alone are no
/// difference here; renaming is left to the user.</item>
/// <item>The <c>IOT - TOP</c> index of an index-organized table is left out: it is the primary key (its own row) and
/// cannot be created or dropped by itself; the organization shows in the object's definition.</item>
/// </list>
/// </remarks>
public static class SchemaDiff
{
    public static SchemaComparison Compare(IReadOnlyList<SchemaSnapshot> sides, CompareOptions options)
    {
        ArgumentNullException.ThrowIfNull(sides);
        ArgumentNullException.ThrowIfNull(options);
        if (sides.Count == 0)
        {
            throw new ArgumentException("A comparison needs at least one side.", nameof(sides));
        }

        if (options.Reference is { } reference && (reference < 0 || reference >= sides.Count))
        {
            throw new ArgumentOutOfRangeException(nameof(options), reference, "The reference is not one of the sides.");
        }

        var context = new Context(sides, options);
        var objects = MatchNames(sides.Select(s => s.Objects.Select(o => o.Name).ToList()).ToList(), context.Preference)
            .Select(match => ObjectRow(context, match))
            .OrderBy(row => row.Name, StringComparer.Ordinal)
            .ToList();

        return new SchemaComparison(sides, options, AssignKeys(objects));
    }

    /// <summary>
    /// The sides and the order in which they are asked for names and first appearances: the reference first, then
    /// from left to right.
    /// </summary>
    private sealed class Context(IReadOnlyList<SchemaSnapshot> sides, CompareOptions options)
    {
        public IReadOnlyList<SchemaSnapshot> Sides { get; } = sides;

        public CompareOptions Options { get; } = options;

        public int Count => Sides.Count;

        public IReadOnlyList<int> Preference { get; } = options.Reference is { } r
            ? [r, .. Enumerable.Range(0, sides.Count).Where(i => i != r)]
            : Enumerable.Range(0, sides.Count).ToList();

        /// <summary>The first side in <see cref="Preference"/> for which <paramref name="has"/> holds.</summary>
        public int First(Func<int, bool> has) => Preference.First(has);
    }

    /// <summary>A row of matched names: per side the index into its list, -1 if it has none.</summary>
    private sealed record NameMatch(string Name, int[] Items);

    /// <summary>A side's entry of a row before states and groups are known.</summary>
    /// <param name="Name">The side's own name when it differs from the row's.</param>
    private readonly record struct Entry(string? Definition, bool OtherCase = false, string? Name = null);

    // ---- objects ---------------------------------------------------------------------------------------------------

    private static CompareRow ObjectRow(Context context, NameMatch match)
    {
        var objects = Enumerable.Range(0, context.Count)
            .Select(s => match.Items[s] < 0 ? null : context.Sides[s].Objects[match.Items[s]])
            .ToArray();
        var entries = objects.Select(o => o is null
            ? new Entry(null)
            : new Entry(CompareDefinitions.Object(o), o.Name != match.Name, o.Name != match.Name ? o.Name : null)).ToList();
        var cells = Cells(context, entries).Select((cell, s) => cell with { Object = objects[s] }).ToList();
        var kind = objects[context.First(s => objects[s] is not null)]!.Kind switch
        {
            TableKind.View => CompareKind.View,
            TableKind.MaterializedView => CompareKind.MaterializedView,
            _ => CompareKind.Table,
        };

        return new CompareRow(match.Name, kind, match.Name, cells, [.. ColumnRows(context, objects), .. ConstraintRows(context, objects), .. IndexRows(context, objects)]);
    }

    // ---- columns ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// In column order of the reference side, else of the first side that has them; columns only on later sides follow
    /// in their order.
    /// </summary>
    private static IEnumerable<CompareRow> ColumnRows(Context context, ObjectSnapshot?[] objects)
    {
        var lists = objects.Select(o => o?.Columns ?? []).ToList();
        var matches = MatchNames(lists.Select(l => l.Select(c => c.Name).ToList()).ToList(), context.Preference);

        // Per side and column: its row (-1 for a repeated name, which only its first column matches).
        var rowOf = lists.Select(l => Enumerable.Repeat(-1, l.Count).ToArray()).ToList();
        for (var m = 0; m < matches.Count; m++)
        {
            for (var s = 0; s < context.Count; s++)
            {
                if (matches[m].Items[s] >= 0)
                {
                    rowOf[s][matches[m].Items[s]] = m;
                }
            }
        }

        var order = new List<int>(matches.Count);
        var placed = new HashSet<int>();
        foreach (var s in context.Preference)
        {
            order.AddRange(rowOf[s].Where(m => m >= 0 && placed.Add(m)).ToList());
        }

        return order.Select(m =>
        {
            var match = matches[m];
            var columns = Enumerable.Range(0, context.Count).Select(s => match.Items[s] < 0 ? null : lists[s][match.Items[s]]).ToArray();
            var entries = Enumerable.Range(0, context.Count).Select(s => columns[s] is not { } c
                ? new Entry(null)
                : new Entry(
                    CompareDefinitions.Column(c, objects[s]!.Kind, context.Options.ColumnOrder ? match.Items[s] + 1 : null),
                    c.Name != match.Name,
                    c.Name != match.Name ? c.Name : null)).ToList();
            var cells = Cells(context, entries).Select((cell, s) => cell with { Column = columns[s] }).ToList();
            return new CompareRow("col/" + match.Name, CompareKind.Column, match.Name, cells, []);
        });
    }

    // ---- constraints and indexes -----------------------------------------------------------------------------------

    /// <summary>A constraint or index of one side, prepared for matching.</summary>
    /// <param name="Generated">Oracle named it: matched by <see cref="Signature"/>, not by name.</param>
    /// <param name="Description">The row's name if it is matched by content.</param>
    private sealed record Item<T>(string Name, bool Generated, string Signature, string Description, string Definition, T Value);

    /// <summary>A row of matched constraints or indexes: per side the index into its item list, -1 if it has none.</summary>
    /// <param name="ByContent">Matched by signature (all generated names); otherwise by the user-given <see cref="Name"/>.</param>
    private sealed record ItemMatch(string Name, bool ByContent, string? Signature, int[] Items);

    private static readonly ConstraintType[] ConstraintOrder = [ConstraintType.PrimaryKey, ConstraintType.Unique, ConstraintType.ForeignKey, ConstraintType.Check];

    private static IEnumerable<CompareRow> ConstraintRows(Context context, ObjectSnapshot?[] objects)
    {
        // The generated NOT NULL checks are part of the column (the snapshot should not have them anyway); view
        // constraints (WITH CHECK OPTION, READ ONLY) are not compared.
        var lists = objects.Select((o, s) => (o?.Constraints ?? [])
            .Where(c => ConstraintOrder.Contains(c.Type) && !c.IsColumnNotNull)
            .Select(c => new Item<ConstraintInfo>(
                c.Name,
                c.GeneratedName,
                CompareDefinitions.ConstraintSignature(c, context.Sides[s].Owner),
                CompareDefinitions.ConstraintSignature(c, context.Sides[s].Owner),
                CompareDefinitions.Constraint(c, context.Sides[s].Owner),
                c))
            .ToList()).ToList();

        // One primary key per table, whatever its name: the first of each side.
        var keys = lists.Select(l => l.Where(i => i.Value.Type == ConstraintType.PrimaryKey).Take(1).ToList()).ToList();
        var others = lists.Select(l => l.Where(i => i.Value.Type != ConstraintType.PrimaryKey).ToList()).ToList();

        var rows = new List<CompareRow>();
        if (keys.Any(k => k.Count > 0))
        {
            var first = keys[context.First(s => keys[s].Count > 0)][0];
            var name = first.Generated ? first.Description : first.Name;
            var match = new ItemMatch(name, true, null, keys.Select(k => k.Count > 0 ? 0 : -1).ToArray());
            rows.Add(ItemRow(context, match, keys, CompareKind.PrimaryKey, "pk", (cell, c) => cell with { Constraint = c }));
        }

        rows.AddRange(MatchItems(context, others)
            .Select(match =>
            {
                var type = Representative(context, match, others).Value.Type;
                return (Type: type, Row: ItemRow(context, match, others, KindOf(type), "con/" + match.Name, (cell, c) => cell with { Constraint = c }));
            })
            .OrderBy(x => Array.IndexOf(ConstraintOrder, x.Type))
            .ThenBy(x => x.Row.Name, StringComparer.Ordinal)
            .Select(x => x.Row));
        return rows;
    }

    private static IEnumerable<CompareRow> IndexRows(Context context, ObjectSnapshot?[] objects)
    {
        var lists = objects.Select(o => (o?.Indexes ?? [])
            .Where(i => i.IndexType != "IOT - TOP")
            .Select(i => new Item<IndexInfo>(
                i.Name,
                i.GeneratedName,
                CompareDefinitions.IndexSignature(i),
                CompareDefinitions.IndexDescription(i),
                CompareDefinitions.Index(i),
                i))
            .ToList()).ToList();

        return MatchItems(context, lists)
            .Select(match => ItemRow(context, match, lists, CompareKind.Index, "idx/" + match.Name, (cell, i) => cell with { Index = i }))
            .OrderBy(row => row.Name, StringComparer.Ordinal);
    }

    private static CompareKind KindOf(ConstraintType type) => type switch
    {
        ConstraintType.PrimaryKey => CompareKind.PrimaryKey,
        ConstraintType.Unique => CompareKind.Unique,
        ConstraintType.ForeignKey => CompareKind.ForeignKey,
        _ => CompareKind.Check,
    };

    private static Item<T> Representative<T>(Context context, ItemMatch match, IReadOnlyList<IReadOnlyList<Item<T>>> lists)
    {
        var s = context.First(s => match.Items[s] >= 0);
        return lists[s][match.Items[s]];
    }

    /// <summary>
    /// User-given names by name; generated names first into a named row with the same content on another side, then
    /// by content among themselves (two equal generated checks on a side pair up with two on another, in order).
    /// </summary>
    private static List<ItemMatch> MatchItems<T>(Context context, IReadOnlyList<IReadOnlyList<Item<T>>> lists)
    {
        var named = lists.Select(l => Enumerable.Range(0, l.Count).Where(j => !l[j].Generated).ToList()).ToList();
        var result = MatchNames(named.Select((l, s) => l.Select(j => lists[s][j].Name).ToList()).ToList(), context.Preference)
            .Select(m => new ItemMatch(m.Name, false, null, m.Items.Select((k, s) => k < 0 ? -1 : named[s][k]).ToArray()))
            .ToList();

        var free = lists.Select(l => Enumerable.Range(0, l.Count).Where(j => l[j].Generated).ToList()).ToList();
        foreach (var match in result)
        {
            var signatures = context.Preference.Where(s => match.Items[s] >= 0).Select(s => lists[s][match.Items[s]].Signature).Distinct().ToList();
            foreach (var s in context.Preference.Where(s => match.Items[s] < 0))
            {
                foreach (var signature in signatures)
                {
                    var found = free[s].FindIndex(j => lists[s][j].Signature == signature);
                    if (found >= 0)
                    {
                        match.Items[s] = free[s][found];
                        free[s].RemoveAt(found);
                        break;
                    }
                }
            }
        }

        foreach (var s in context.Preference)
        {
            foreach (var j in free[s])
            {
                var item = lists[s][j];
                var match = result.FirstOrDefault(m => m.ByContent && m.Signature == item.Signature && m.Items[s] < 0);
                if (match is null)
                {
                    match = new ItemMatch(item.Description, true, item.Signature, Enumerable.Repeat(-1, context.Count).ToArray());
                    result.Add(match);
                }

                match.Items[s] = j;
            }
        }

        return result;
    }

    private static CompareRow ItemRow<T>(
        Context context, ItemMatch match, IReadOnlyList<IReadOnlyList<Item<T>>> lists, CompareKind kind, string key, Func<CompareCell, T, CompareCell> payload)
    {
        var items = Enumerable.Range(0, context.Count).Select(s => match.Items[s] < 0 ? null : lists[s][match.Items[s]]).ToArray();
        var entries = items.Select(i => i is null
            ? new Entry(null)
            : new Entry(
                i.Definition,
                OtherCase: !match.ByContent && !i.Generated && i.Name != match.Name,
                Name: i.Name != match.Name ? i.Name : null)).ToList();
        var cells = Cells(context, entries).Select((cell, s) => items[s] is { } i ? payload(cell, i.Value) : cell).ToList();
        return new CompareRow(key, kind, match.Name, cells, []);
    }

    // ---- matching by name ------------------------------------------------------------------------------------------

    /// <summary>
    /// Rows of names: exact names match; a side without the exact name joins with a name differing only in letter case
    /// (<see cref="CellState.OtherCase"/>). The row's name is the reference side's, else the first side's from the left.
    /// If a side has several names differing only in case, each spelling becomes a row of its own with exact matches.
    /// The order of the result is not meaningful.
    /// </summary>
    private static List<NameMatch> MatchNames(IReadOnlyList<IReadOnlyList<string>> names, IReadOnlyList<int> preference)
    {
        // Per side: exact name → index (the first if a name repeats); spellings per case-insensitive name.
        var exact = names.Select(l =>
        {
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var j = 0; j < l.Count; j++)
            {
                map.TryAdd(l[j], j);
            }

            return map;
        }).ToList();

        var spellings = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in preference)
        {
            foreach (var name in exact[s].Keys.OrderBy(n => exact[s][n]))
            {
                if (!spellings.TryGetValue(name, out var list))
                {
                    spellings[name] = list = [];
                }

                if (!list.Contains(name))
                {
                    list.Add(name);
                }
            }
        }

        var result = new List<NameMatch>();
        foreach (var variants in spellings.Values)
        {
            var conflict = exact.Any(map => variants.Count(map.ContainsKey) > 1);
            if (conflict)
            {
                result.AddRange(variants.Select(v => new NameMatch(v, exact.Select(map => map.GetValueOrDefault(v, -1)).ToArray())));
            }
            else
            {
                // Every side has at most one spelling: one row, named as on the first side in preference order.
                result.Add(new NameMatch(variants[0], exact.Select(map => variants.Where(map.ContainsKey).Select(v => map[v]).DefaultIfEmpty(-1).First()).ToArray()));
            }
        }

        return result;
    }

    // ---- states and groups -----------------------------------------------------------------------------------------

    /// <summary>
    /// Groups by equal definition in order of first appearance from the left; states against the reference, or – without
    /// one – Same when all sides that have the object agree.
    /// </summary>
    private static List<CompareCell> Cells(Context context, IReadOnlyList<Entry> entries)
    {
        var distinct = new List<string>();
        var groups = entries.Select(e =>
        {
            if (e.Definition is not { } definition)
            {
                return -1;
            }

            var group = distinct.IndexOf(definition);
            if (group < 0)
            {
                distinct.Add(definition);
                group = distinct.Count - 1;
            }

            return group;
        }).ToList();

        return entries.Select((e, s) =>
        {
            var state = e.Definition is null ? CellState.Missing
                : e.OtherCase ? CellState.OtherCase
                : context.Options.Reference is { } r ? (groups[r] >= 0 && groups[s] == groups[r] ? CellState.Same : CellState.Different)
                : distinct.Count == 1 ? CellState.Same
                : CellState.Different;
            return new CompareCell(state, groups[s], e.Definition) { Name = e.Name };
        }).ToList();
    }

    // ---- keys ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Object keys are the names, child keys <c>&lt;object&gt;/&lt;local key&gt;</c> (<c>col/…</c>, <c>pk</c>, <c>con/…</c>,
    /// <c>idx/…</c>); a key already taken (names containing '/', two equal generated checks) gets <c>#2</c>, <c>#3</c> ….
    /// </summary>
    private static List<CompareRow> AssignKeys(List<CompareRow> objects)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        var keyed = objects.Select(o => o with { Key = Unique(used, o.Name) }).ToList();
        return keyed.Select(o => o with { Children = o.Children.Select(c => c with { Key = Unique(used, $"{o.Key}/{c.Key}") }).ToList() }).ToList();
    }

    private static string Unique(HashSet<string> used, string key)
    {
        if (used.Add(key))
        {
            return key;
        }

        for (var n = 2; ; n++)
        {
            var candidate = $"{key}#{n}";
            if (used.Add(candidate))
            {
                return candidate;
            }
        }
    }
}
