using FerretSharp.Core.Connections;
using FerretSharp.Core.Data;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Display;

/// <summary>What a foreign key cell shows beside its value.</summary>
/// <param name="Text">The display value as text; null if it is NULL.</param>
/// <param name="NotFound">No row of the target has the value – a dangling reference (many relationships have no constraint).</param>
public sealed record LookedUpName(string? Text, bool NotFound);

/// <summary>
/// One lookup to run: keys of one link not known yet, at most <see cref="Query.QueryBuilder.MaxLookupKeys"/>. With a
/// <paramref name="Part"/> the keys are values of that display column, looked up one level deeper (WP-65,
/// <see cref="DisplayPart.Via"/>).
/// </summary>
public sealed record DisplayLookup(DisplayLink Link, IReadOnlyList<object> Keys, int? Part = null)
{
    /// <summary>The link whose target is read: <see cref="Link"/>, or the part's link one level deeper.</summary>
    public DisplayLink Source => Part is { } part ? Link.Parts[part].Via! : Link;
}

/// <summary>A lookup of <see cref="DisplayNames.FillAsync"/> that failed: the warning for the user and the error for the log.</summary>
public sealed record NameLookupFailure(string Warning, DatabaseException Error);

/// <summary>
/// The names of one grid (WP-31): which values of its foreign key columns were looked up, what was found, and which
/// keys are still to be looked up. Lookups are collected per block of rows, so each key is asked for once; a link that
/// failed (timeout, no grant) is switched off for the grid. Kept until the grid reads from its first row again
/// (<see cref="Clear"/>), so names follow writes, commit, rollback and F5. Used from the UI thread only.
/// <para>
/// A name joins the values of the target's display columns (WP-65, <see cref="Separator"/>). A display column that is a
/// foreign key itself (<see cref="DisplayPart.Via"/>) shows the name of its target, looked up after the first lookup
/// found its values (<see cref="Missing"/> then returns those lookups too); until then, if it is not found or its lookup
/// failed, the value itself.
/// </para>
/// </summary>
public sealed class DisplayNames
{
    /// <summary>Longer display values are shortened: the name is a hint beside the value, not the value.</summary>
    public const int MaxTextLength = 60;

    /// <summary>Between the values of several display columns (the user's decision, WP-65); <c>·</c> separates value and name.</summary>
    public const string Separator = " - ";

    /// <summary>A lookup gives up after this; the link is then switched off for the grid.</summary>
    public static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(5);

    public static readonly DisplayNames None = new([], (_, _, _) => null);

    private readonly Dictionary<int, LinkState> _byColumn;
    private readonly Func<TableDetails, int, object?, string?> _format;

    /// <param name="format">
    /// A value of a column of a target as text (in the UI through the target's presentation: enum members by name).
    /// </param>
    public DisplayNames(IReadOnlyList<DisplayLink> links, Func<TableDetails, int, object?, string?> format)
    {
        Links = links;
        _format = format;
        _byColumn = links.ToDictionary(l => l.Column, l => new LinkState(l));
    }

    public IReadOnlyList<DisplayLink> Links { get; }

    public bool IsEmpty => Links.Count == 0;

    /// <summary>The column shows names (it has a link that was not switched off).</summary>
    public bool Shows(int column) => _byColumn.TryGetValue(column, out var state) && state.Keys.Disabled is null;

    /// <summary>The lookup is still wanted: its link (and, one level deeper, its part) was not switched off or replaced.</summary>
    public bool Wants(DisplayLookup lookup) => KeysOf(lookup) is { Disabled: null } && Shows(lookup.Link.Column);

    /// <summary>Why the column's link was switched off; null if it was not (or there is none).</summary>
    internal string? DisabledReason(int column) => _byColumn.GetValueOrDefault(column)?.Keys.Disabled;

    /// <summary>What to show beside a value of the column; null if nothing (no link, NULL, not looked up yet).</summary>
    public LookedUpName? NameOf(int column, object? value)
    {
        if (!_byColumn.TryGetValue(column, out var state) || state.Keys.Disabled is not null || state.Keys.KeyOf(value) is not { } key)
        {
            return null;
        }

        if (state.Keys.Found.ContainsKey(key))
        {
            return new LookedUpName(TextOf(state, key), false);
        }

        return state.Keys.NotFound.Contains(key) ? new LookedUpName(null, true) : null;
    }

    /// <summary>
    /// The lookups the rows need: per link the values not known and not asked for yet (from then on they count as asked
    /// for), and the values a found name needs one level deeper. <paramref name="rows"/> are values in the table's column
    /// order – as shown, with pending edits.
    /// </summary>
    public IReadOnlyList<DisplayLookup> Missing(IEnumerable<IReadOnlyList<object?>> rows)
    {
        if (IsEmpty)
        {
            return [];
        }

        var wanted = _byColumn.Values.Where(s => s.Keys.Disabled is null).ToDictionary(s => s, _ => new List<object>());
        foreach (var values in rows)
        {
            foreach (var (state, keys) in wanted)
            {
                if (state.Keys.Ask(values[state.Link.Column]) is { } bind)
                {
                    keys.Add(bind);
                }
            }
        }

        var lookups = wanted
            .SelectMany(w => w.Value.Chunk(Query.QueryBuilder.MaxLookupKeys).Select(chunk => new DisplayLookup(w.Key.Link, chunk)))
            .ToList();
        foreach (var state in wanted.Keys)
        {
            foreach (var (part, keys) in state.Deeper())
            {
                lookups.AddRange(keys.Wanted.Chunk(Query.QueryBuilder.MaxLookupKeys).Select(chunk => new DisplayLookup(state.Link, chunk, part)));
                keys.Wanted.Clear();
            }
        }

        return lookups;
    }

    /// <summary>The rows a lookup found; its other keys have no row. Values one level deeper are asked for next (<see cref="Missing"/>).</summary>
    public void Found(DisplayLookup lookup, IReadOnlyList<LookupRow> rows)
    {
        if (!_byColumn.TryGetValue(lookup.Link.Column, out var state) || state.Link != lookup.Link || KeysOf(lookup) is not { } keys)
        {
            return; // a lookup of links planned before
        }

        keys.Timeouts = 0;
        foreach (var row in rows)
        {
            if (keys.KeyOf(row.Key) is { } key)
            {
                keys.Found[key] = row.Values;
                if (lookup.Part is null)
                {
                    state.Texts.Remove(key);
                    foreach (var (part, deeper) in state.Deeper())
                    {
                        if (deeper.Ask(row.Values[part]) is { } bind)
                        {
                            deeper.Wanted.Add(bind);
                        }
                    }
                }
            }
        }

        keys.Answered(lookup.Keys);
        if (lookup.Part is not null)
        {
            state.Texts.Clear(); // names that showed the value now show the name found for it
        }
    }

    /// <summary>
    /// Timeouts in a row after which a link is switched off. The timeout also counts the wait for the workspace's session,
    /// which a long statement of the SQL editor or a slow block can hold: one timeout is no reason to give up.
    /// </summary>
    public const int MaxTimeouts = 3;

    /// <summary>
    /// The lookup took too long. Its keys stay asked for, so it can be run again; after <see cref="MaxTimeouts"/> in a row the
    /// link (or its part one level deeper) is switched off (<see cref="Failed"/>). True if it was.
    /// </summary>
    public bool TimedOut(DisplayLookup lookup, string reason)
    {
        if (!_byColumn.TryGetValue(lookup.Link.Column, out var state) || state.Link != lookup.Link || KeysOf(lookup) is not { } keys)
        {
            return false;
        }

        if (++keys.Timeouts < MaxTimeouts)
        {
            return false;
        }

        Failed(lookup, reason);
        return true;
    }

    /// <summary>
    /// The lookup failed: the column shows no names in this grid any more – or, for a lookup one level deeper, its part
    /// shows the value instead of the name.
    /// </summary>
    public void Failed(DisplayLookup lookup, string reason)
    {
        if (_byColumn.TryGetValue(lookup.Link.Column, out var state) && state.Link == lookup.Link && KeysOf(lookup) is { } keys)
        {
            keys.Disabled ??= reason;
            keys.Pending.Clear();
            keys.Wanted.Clear();
            state.Texts.Clear();
        }
    }

    /// <summary>
    /// The lookup was not run (the grid read from its first row meanwhile, the session was reset): its keys may be asked for
    /// again – the grid's rows ask again, keys one level deeper are asked for with the next lookups.
    /// </summary>
    public void Dropped(DisplayLookup lookup)
    {
        if (_byColumn.TryGetValue(lookup.Link.Column, out var state) && state.Link == lookup.Link && KeysOf(lookup) is { } keys)
        {
            if (lookup.Part is null)
            {
                keys.Unask(lookup.Keys);
            }
            else
            {
                keys.Wanted.AddRange(lookup.Keys); // still pending: only Found gives a deeper key, nobody would ask again
            }
        }
    }

    /// <summary>Whether a row (values in column order) shows one of the lookup's keys – it is redrawn when the names come.</summary>
    public bool Touches(DisplayLookup lookup, IReadOnlyList<object?> values)
    {
        if (!_byColumn.TryGetValue(lookup.Link.Column, out var state) || state.Keys.KeyOf(values[lookup.Link.Column]) is not { } key)
        {
            return false;
        }

        if (lookup.Part is not { } part)
        {
            return lookup.Keys.Any(k => Equals(state.Keys.KeyOf(k), key));
        }

        // One level deeper: the rows whose name holds one of the keys.
        return state.Keys.Found.TryGetValue(key, out var found) && KeysOf(lookup) is { } deeper && deeper.KeyOf(found[part]) is { } deeperKey
               && lookup.Keys.Any(k => Equals(deeper.KeyOf(k), deeperKey));
    }

    /// <summary>Forgets what was looked up (the grid reads from its first row again); switched-off links stay off.</summary>
    public void Clear()
    {
        foreach (var state in _byColumn.Values)
        {
            state.Clear();
        }
    }

    /// <summary>
    /// The same links, nothing looked up yet – and no link switched off: the export of all rows (WP-54) looks up its own
    /// names while it reads, off the UI thread, so it formats with a <paramref name="format"/> of its own.
    /// </summary>
    public DisplayNames Fresh(Func<TableDetails, int, object?, string?> format) => new(Links, format);

    /// <summary>
    /// What was looked up so far, as a copy: the export of the selected rows (A13) writes in the background while the grid
    /// goes on looking up names.
    /// </summary>
    public DisplayNames Copy()
    {
        var copy = new DisplayNames(Links, _format);
        foreach (var (column, state) in _byColumn)
        {
            state.CopyTo(copy._byColumn[column]);
        }

        return copy;
    }

    /// <summary>
    /// Looks up what <paramref name="rows"/> need, one lookup after the other, before they are written (the export of all
    /// rows, WP-54: it reads the rows itself, through the lookup of its read) – one level deeper too. A link whose lookup
    /// fails is switched off and named in the failures; a lost connection is no failure of a link and ends the export.
    /// </summary>
    /// <exception cref="DatabaseException">The connection was lost (<see cref="DatabaseException.IsConnectionLost"/>).</exception>
    public async Task<IReadOnlyList<NameLookupFailure>> FillAsync(
        IRowLookup lookup, IEnumerable<IReadOnlyList<object?>> rows, CancellationToken cancellationToken)
    {
        List<NameLookupFailure>? failures = null;
        for (var missing = Missing(rows); missing.Count > 0; missing = Missing([]))
        {
            foreach (var asked in missing)
            {
                if (!Wants(asked))
                {
                    continue; // a further lookup of a link that failed (more keys than one lookup takes)
                }

                var source = asked.Source;
                try
                {
                    Found(asked, await lookup.LookupAsync(source.Target, source.KeyColumn, source.DisplayColumns, asked.Keys, cancellationToken));
                }
                catch (DatabaseException ex) when (!ex.IsConnectionLost)
                {
                    Failed(asked, ex.Message);
                    var warning = TextFormat.Format(DataText.ExportNamesMissing, $"{source.Target.Table.DisplayName}.{source.Label}", ex.Display);
                    (failures ??= []).Add(new NameLookupFailure(warning, ex));
                }
            }
        }

        return failures ?? [];
    }

    /// <summary>
    /// Runs a lookup; null if it took longer than <paramref name="timeout"/> – the statement is then cancelled on the server
    /// (<see cref="TimedQuery"/>, as for incoming FK counts).
    /// </summary>
    public static Task<IReadOnlyList<LookupRow>?> LookupAsync(
        IDataAccess data, DisplayLookup lookup, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var source = lookup.Source;
        return TimedQuery.RunAsync(token => data.LookupAsync(source.Target, source.KeyColumn, source.DisplayColumns, lookup.Keys, token), timeout, cancellationToken);
    }

    /// <summary>
    /// The texts of a name's parts joined in their order (<see cref="Separator"/>); NULL and blank parts are left out, null if
    /// none is left. Not shortened.
    /// </summary>
    public static string? Compose(IEnumerable<string?> parts) =>
        string.Join(Separator, parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.Trim())) is { Length: > 0 } text ? text : null;

    /// <summary>The keys a lookup asked for: the link's own, or those of its part one level deeper; null if there are none.</summary>
    private KeyedValues? KeysOf(DisplayLookup lookup)
    {
        if (!_byColumn.TryGetValue(lookup.Link.Column, out var state))
        {
            return null;
        }

        return lookup.Part is { } part ? state.DeeperOf(part) : state.Keys;
    }

    /// <summary>The name of a found key: the parts formatted (one level deeper: the name found there), joined, shortened; cached.</summary>
    private string? TextOf(LinkState state, object key)
    {
        if (state.Texts.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var values = state.Keys.Found[key];
        var link = state.Link;
        var parts = new string?[link.Parts.Count];
        for (var i = 0; i < parts.Length; i++)
        {
            var value = values[i];
            parts[i] = state.DeeperOf(i) is { Disabled: null } deeper && deeper.KeyOf(value) is { } deeperKey && deeper.Found.TryGetValue(deeperKey, out var found)
                ? Compose(deeper.Link.Parts.Select((p, j) => _format(deeper.Link.Target, p.Column, found[j])))
                : value is null ? null : _format(link.Target, link.Parts[i].Column, value);
        }

        var text = Shorten(Compose(parts));
        state.Texts[key] = text;
        return text;
    }

    private static string? Shorten(string? text)
    {
        if (text is null)
        {
            return null;
        }

        text = text.ReplaceLineEndings(" ");
        return text.Length > MaxTextLength ? text[..(MaxTextLength - 1)] + "…" : text;
    }

    /// <summary>A link: its own keys, the keys of its parts one level deeper, and the names composed so far.</summary>
    private sealed class LinkState
    {
        /// <summary>By part index; null for a part that shows its value.</summary>
        private readonly KeyedValues?[] _deeper;

        public LinkState(DisplayLink link)
        {
            Link = link;
            Keys = new KeyedValues(link);
            _deeper = [.. link.Parts.Select(p => p.Via is { } via ? new KeyedValues(via) : null)];
        }

        public DisplayLink Link { get; }

        public KeyedValues Keys { get; }

        /// <summary>Composed names by key; dropped when what they are made of changes.</summary>
        public Dictionary<object, string?> Texts { get; } = [];

        public KeyedValues? DeeperOf(int part) => part >= 0 && part < _deeper.Length ? _deeper[part] : null;

        /// <summary>The parts looked up one level deeper that are still on.</summary>
        public IEnumerable<(int Part, KeyedValues Keys)> Deeper()
        {
            for (var i = 0; i < _deeper.Length; i++)
            {
                if (_deeper[i] is { Disabled: null } keys)
                {
                    yield return (i, keys);
                }
            }
        }

        public void Clear()
        {
            Keys.Clear();
            foreach (var deeper in _deeper)
            {
                deeper?.Clear();
            }

            Texts.Clear();
        }

        public void CopyTo(LinkState target)
        {
            Keys.CopyTo(target.Keys);
            for (var i = 0; i < _deeper.Length; i++)
            {
                _deeper[i]?.CopyTo(target._deeper[i]!);
            }
        }
    }

    /// <summary>The keys of one link's target: found (with the display values), not found, asked for, and its state.</summary>
    private sealed class KeyedValues(DisplayLink link)
    {
        /// <summary>CHAR on either side: values compare without their blank padding.</summary>
        private readonly bool _padded = link.SourcePadded || IsPadded(link.Key.DataType);

        public DisplayLink Link { get; } = link;

        /// <summary>Key → the display columns' values of its row.</summary>
        public Dictionary<object, IReadOnlyList<object?>> Found { get; } = [];

        public HashSet<object> NotFound { get; } = [];

        /// <summary>Asked for, answer outstanding: normalized key → value as shown.</summary>
        public Dictionary<object, object> Pending { get; } = [];

        /// <summary>One level deeper: keys asked for that no lookup was made for yet (<see cref="DisplayNames.Missing"/> takes them).</summary>
        public List<object> Wanted { get; } = [];

        public string? Disabled { get; set; }

        /// <summary>Lookups that timed out since the last one that answered.</summary>
        public int Timeouts { get; set; }

        /// <summary>
        /// A value as a dictionary key: RAW by content, numbers as decimal, CHAR without its padding; null for NULL and for
        /// values that cannot be looked up (NUMBER beyond 28 digits, floating point).
        /// </summary>
        public object? KeyOf(object? value) => value switch
        {
            null => null,
            string s => _padded ? s.TrimEnd(' ') : s,
            decimal d => d,
            int or long or short => Convert.ToDecimal(value, System.Globalization.CultureInfo.InvariantCulture),
            DateTime dt => dt,
            byte[] bytes => "0x" + Convert.ToHexString(bytes),
            _ => null,
        };

        /// <summary>The value to bind if it is to be asked for now (not known, not asked for yet); it counts as asked for from then on.</summary>
        public object? Ask(object? value) =>
            KeyOf(value) is { } key && !Found.ContainsKey(key) && !NotFound.Contains(key) && Pending.TryAdd(key, value!) ? BindValue(value!) : null;

        /// <summary>A lookup answered: its keys without a row are not found.</summary>
        public void Answered(IEnumerable<object> keys)
        {
            foreach (var value in keys)
            {
                if (KeyOf(value) is { } key)
                {
                    Pending.Remove(key);
                    if (!Found.ContainsKey(key))
                    {
                        NotFound.Add(key);
                    }
                }
            }
        }

        /// <summary>The keys may be asked for again.</summary>
        public void Unask(IEnumerable<object> keys)
        {
            foreach (var value in keys)
            {
                if (KeyOf(value) is { } key)
                {
                    Pending.Remove(key);
                }
            }
        }

        public void Clear()
        {
            Found.Clear();
            NotFound.Clear();
            Pending.Clear();
            Wanted.Clear();
        }

        public void CopyTo(KeyedValues target)
        {
            target.Disabled = Disabled;
            foreach (var (key, values) in Found)
            {
                target.Found[key] = values;
            }

            target.NotFound.UnionWith(NotFound);
        }

        /// <summary>The value as bound: a padded CHAR value without its padding if the target's key is no CHAR.</summary>
        private object BindValue(object value) =>
            value is string s && Link.SourcePadded && !IsPadded(Link.Key.DataType) ? s.TrimEnd(' ') : value;

        private static bool IsPadded(string? dataType) => dataType is not null && OracleTypes.IsFixedChar(dataType);
    }
}
