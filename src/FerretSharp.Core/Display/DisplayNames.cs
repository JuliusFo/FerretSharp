using FerretSharp.Core.Data;

namespace FerretSharp.Core.Display;

/// <summary>What a foreign key cell shows beside its value.</summary>
/// <param name="Text">The display value as text; null if it is NULL.</param>
/// <param name="NotFound">No row of the target has the value – a dangling reference (many relationships have no constraint).</param>
public sealed record DisplayName(string? Text, bool NotFound);

/// <summary>One lookup to run: keys of one link not known yet, at most <see cref="Query.QueryBuilder.MaxLookupKeys"/>.</summary>
public sealed record DisplayLookup(DisplayLink Link, IReadOnlyList<object> Keys);

/// <summary>
/// The names of one grid (WP-31): which values of its foreign key columns were looked up, what was found, and which
/// keys are still to be looked up. Lookups are collected per block of rows, so each key is asked for once; a link that
/// failed (timeout, no grant) is switched off for the grid. Kept until the grid reads from its first row again
/// (<see cref="Clear"/>), so names follow writes, commit, rollback and F5. Used from the UI thread only.
/// </summary>
public sealed class DisplayNames
{
    /// <summary>Longer display values are shortened: the name is a hint beside the value, not the value.</summary>
    public const int MaxTextLength = 60;

    /// <summary>A lookup gives up after this; the link is then switched off for the grid.</summary>
    public static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(5);

    public static readonly DisplayNames None = new([], (_, _) => null);

    private readonly Dictionary<int, LinkState> _byColumn;
    private readonly Func<DisplayLink, object?, string?> _format;

    /// <param name="format">The display value as text (in the UI through the target's presentation: enum members by name).</param>
    public DisplayNames(IReadOnlyList<DisplayLink> links, Func<DisplayLink, object?, string?> format)
    {
        Links = links;
        _format = format;
        _byColumn = links.ToDictionary(l => l.Column, l => new LinkState(l));
    }

    public IReadOnlyList<DisplayLink> Links { get; }

    public bool IsEmpty => Links.Count == 0;

    /// <summary>The column shows names (it has a link that was not switched off).</summary>
    public bool Shows(int column) => _byColumn.TryGetValue(column, out var state) && state.Disabled is null;

    /// <summary>Why the column's link was switched off; null if it was not (or there is none).</summary>
    public string? DisabledReason(int column) => _byColumn.GetValueOrDefault(column)?.Disabled;

    /// <summary>What to show beside a value of the column; null if nothing (no link, NULL, not looked up yet).</summary>
    public DisplayName? NameOf(int column, object? value)
    {
        if (!_byColumn.TryGetValue(column, out var state) || state.Disabled is not null || state.KeyOf(value) is not { } key)
        {
            return null;
        }

        if (state.Found.TryGetValue(key, out var text))
        {
            return new DisplayName(text, false);
        }

        return state.NotFound.Contains(key) ? new DisplayName(null, true) : null;
    }

    /// <summary>
    /// The lookups the rows need: per link the values not known and not asked for yet (from then on they count as asked
    /// for). <paramref name="rows"/> are values in the table's column order – as shown, with pending edits.
    /// </summary>
    public IReadOnlyList<DisplayLookup> Missing(IEnumerable<IReadOnlyList<object?>> rows)
    {
        if (IsEmpty)
        {
            return [];
        }

        var wanted = _byColumn.Values.Where(s => s.Disabled is null).ToDictionary(s => s, _ => new List<object>());
        foreach (var values in rows)
        {
            foreach (var (state, keys) in wanted)
            {
                var value = values[state.Link.Column];
                if (state.KeyOf(value) is { } key && !state.Found.ContainsKey(key) && !state.NotFound.Contains(key) && state.Pending.TryAdd(key, value!))
                {
                    keys.Add(state.BindValue(value!));
                }
            }
        }

        return wanted
            .SelectMany(w => w.Value.Chunk(Query.QueryBuilder.MaxLookupKeys).Select(chunk => new DisplayLookup(w.Key.Link, chunk)))
            .ToList();
    }

    /// <summary>The rows a lookup found; its other keys have no row.</summary>
    public void Found(DisplayLookup lookup, IReadOnlyList<LookupRow> rows)
    {
        if (!_byColumn.TryGetValue(lookup.Link.Column, out var state) || state.Link != lookup.Link)
        {
            return; // a lookup of links planned before
        }

        state.Timeouts = 0;
        foreach (var row in rows)
        {
            if (state.KeyOf(row.Key) is { } key)
            {
                state.Found[key] = Shorten(_format(state.Link, row.Display));
            }
        }

        foreach (var value in lookup.Keys)
        {
            if (state.KeyOf(value) is { } key)
            {
                state.Pending.Remove(key);
                if (!state.Found.ContainsKey(key))
                {
                    state.NotFound.Add(key);
                }
            }
        }
    }

    /// <summary>
    /// Timeouts in a row after which a link is switched off. The timeout also counts the wait for the workspace's session,
    /// which a long statement of the SQL editor or a slow block can hold: one timeout is no reason to give up.
    /// </summary>
    public const int MaxTimeouts = 3;

    /// <summary>
    /// The lookup took too long. Its keys stay asked for, so it can be run again; after <see cref="MaxTimeouts"/> in a row the
    /// link is switched off (<see cref="Failed"/>). True if it was.
    /// </summary>
    public bool TimedOut(DisplayLookup lookup, string reason)
    {
        if (!_byColumn.TryGetValue(lookup.Link.Column, out var state) || state.Link != lookup.Link)
        {
            return false;
        }

        if (++state.Timeouts < MaxTimeouts)
        {
            return false;
        }

        Failed(lookup, reason);
        return true;
    }

    /// <summary>The lookup failed: the column shows no names in this grid any more.</summary>
    public void Failed(DisplayLookup lookup, string reason)
    {
        if (_byColumn.TryGetValue(lookup.Link.Column, out var state) && state.Link == lookup.Link)
        {
            state.Disabled ??= reason;
            state.Pending.Clear();
        }
    }

    /// <summary>The lookup was not run (the grid read from its first row meanwhile): its keys may be asked for again.</summary>
    public void Dropped(DisplayLookup lookup)
    {
        if (_byColumn.TryGetValue(lookup.Link.Column, out var state) && state.Link == lookup.Link)
        {
            foreach (var value in lookup.Keys)
            {
                if (state.KeyOf(value) is { } key)
                {
                    state.Pending.Remove(key);
                }
            }
        }
    }

    /// <summary>Whether a row (values in column order) shows one of the lookup's keys – it is redrawn when the names come.</summary>
    public bool Touches(DisplayLookup lookup, IReadOnlyList<object?> values)
    {
        if (!_byColumn.TryGetValue(lookup.Link.Column, out var state) || state.KeyOf(values[lookup.Link.Column]) is not { } key)
        {
            return false;
        }

        return lookup.Keys.Any(k => Equals(state.KeyOf(k), key));
    }

    /// <summary>Forgets what was looked up (the grid reads from its first row again); switched-off links stay off.</summary>
    public void Clear()
    {
        foreach (var state in _byColumn.Values)
        {
            state.Found.Clear();
            state.NotFound.Clear();
            state.Pending.Clear();
        }
    }

    /// <summary>
    /// Runs a lookup; null if it took longer than <paramref name="timeout"/> – the statement is then cancelled on the server
    /// (as for incoming FK counts, <see cref="Query.FkNavigation.CountAsync"/>).
    /// </summary>
    public static async Task<IReadOnlyList<LookupRow>?> LookupAsync(
        IDataAccess data, DisplayLookup lookup, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var link = lookup.Link;
        var rows = data.LookupAsync(link.Target, link.KeyColumn, link.DisplayColumn, lookup.Keys, cts.Token);
        try
        {
            return await rows.WaitAsync(timeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            await cts.CancelAsync();
            return null;
        }
        finally
        {
            // Dispose only once the statement has ended; its cancel registration uses the token. A lookup that fails after
            // the timeout is observed here, not left as an unobserved task exception.
            _ = rows.ContinueWith(finished =>
            {
                _ = finished.Exception;
                cts.Dispose();
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
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

    private sealed class LinkState(DisplayLink link)
    {
        /// <summary>CHAR on either side: values compare without their blank padding.</summary>
        private readonly bool _padded = link.SourcePadded || IsPadded(link.Key.DataType);

        public DisplayLink Link { get; } = link;

        public Dictionary<object, string?> Found { get; } = [];

        public HashSet<object> NotFound { get; } = [];

        /// <summary>Asked for, answer outstanding: normalized key → value as shown.</summary>
        public Dictionary<object, object> Pending { get; } = [];

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

        /// <summary>The value as bound: a padded CHAR value without its padding if the target's key is no CHAR.</summary>
        public object BindValue(object value) =>
            value is string s && Link.SourcePadded && !IsPadded(Link.Key.DataType) ? s.TrimEnd(' ') : value;

        internal static bool IsPadded(string? dataType) => dataType is "CHAR" or "NCHAR";
    }
}
