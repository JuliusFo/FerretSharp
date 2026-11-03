namespace FerretSharp.Core.Workspaces;

/// <summary>
/// A pinned column of a query result (WP-73), by the name the query gives it; <paramref name="Occurrence"/> counts the
/// columns of that name before it (0 for the first), so the second <c>ID</c> of a join can be pinned alone.
/// </summary>
public sealed record ResultColumnPin(string Name, int Occurrence = 0);

/// <summary>
/// Columns of a query result pinned to the left edge of its grid (WP-73), per SQL editor or LINQ console tab: in the order
/// they were pinned, for every result of the tab. A pin whose column the shown result lacks stays – a script's other
/// result may have it –, so the list is capped at <see cref="MaxPins"/>, the oldest pins going first. A result has no
/// always pinned key, unlike a table (<see cref="ColumnPinning"/>).
/// </summary>
public static class ResultPinning
{
    /// <summary>Pins kept per tab (decision of the user): enough for a script's results, nothing piles up.</summary>
    public const int MaxPins = 30;

    /// <summary>The pin of result column <paramref name="column"/> of the result whose columns are named <paramref name="names"/>.</summary>
    public static ResultColumnPin PinOf(IReadOnlyList<string> names, int column) =>
        new(names[column], names.Take(column).Count(n => string.Equals(n, names[column], StringComparison.Ordinal)));

    public static bool IsPinned(IReadOnlyList<ResultColumnPin> pins, IReadOnlyList<string> names, int column) =>
        pins.Contains(PinOf(names, column));

    /// <summary>The pinned columns of this result in pin order.</summary>
    public static IReadOnlyList<int> Pinned(IReadOnlyList<ResultColumnPin> pins, IReadOnlyList<string> names)
    {
        var columns = Enumerable.Range(0, names.Count).ToDictionary(i => PinOf(names, i));
        return [.. pins.Where(columns.ContainsKey).Select(p => columns[p])];
    }

    /// <summary>Result columns in display order: the pinned ones in pin order, then the rest in the query's order.</summary>
    public static IReadOnlyList<int> DisplayOrder(IReadOnlyList<ResultColumnPin> pins, IReadOnlyList<string> names)
    {
        var pinned = Pinned(pins, names);
        return [.. pinned, .. Enumerable.Range(0, names.Count).Except(pinned)];
    }

    /// <summary>Releases a pinned column, or pins it after the others.</summary>
    public static IReadOnlyList<ResultColumnPin> Toggle(IReadOnlyList<ResultColumnPin> pins, IReadOnlyList<string> names, int column)
    {
        var pin = PinOf(names, column);
        return pins.Contains(pin) ? [.. pins.Where(p => p != pin)] : Normalize([.. pins, pin]);
    }

    /// <summary>
    /// The pins after the user pinned, released or reordered columns of this result by dragging: <paramref name="pinned"/>
    /// in display order replace this result's pins; those of columns it does not have stay. Beyond <see cref="MaxPins"/> the
    /// oldest pins go, never one just pinned – wherever it was dropped (review of WP-73: dropped in front, it went first).
    /// </summary>
    public static IReadOnlyList<ResultColumnPin> FromGrid(IReadOnlyList<ResultColumnPin> pins, IReadOnlyList<string> names, IEnumerable<int> pinned)
    {
        var own = Enumerable.Range(0, names.Count).Select(i => PinOf(names, i)).ToHashSet();
        var after = Normalize([.. pins.Where(p => !own.Contains(p)), .. pinned.Select(i => PinOf(names, i)).Distinct()], cap: false);
        if (after.Count <= MaxPins)
        {
            return after;
        }

        // The pins kept from before are dropped in the order they were pinned in.
        var dropped = pins.Where(after.Contains).Take(after.Count - MaxPins).ToHashSet();
        return Normalize(after.Where(p => !dropped.Contains(p))); // the cap still holds if more than that were new
    }

    /// <summary>Pins as saved: no duplicates, no negative occurrence, at most <see cref="MaxPins"/> (the newest).</summary>
    public static IReadOnlyList<ResultColumnPin> Normalize(IEnumerable<ResultColumnPin> pins) => Normalize(pins, cap: true);

    private static IReadOnlyList<ResultColumnPin> Normalize(IEnumerable<ResultColumnPin> pins, bool cap)
    {
        var kept = pins.Where(p => p is { Name: not null, Occurrence: >= 0 }).Distinct().ToList();
        return cap && kept.Count > MaxPins ? kept[^MaxPins..] : kept;
    }
}
