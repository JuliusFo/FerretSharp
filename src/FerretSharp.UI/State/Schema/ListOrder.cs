namespace FerretSharp.UI.State;

/// <summary>
/// The order of a list view's rows by a header click (columns, usage of the columns): ascending, descending, then back
/// to the list's own order (the schema's). Text in the UI's culture without case, as other lists; rows without a value
/// (a column without property or comment) stay last in both directions, equal ones in the list's order. Kept while the
/// view is mounted, not saved.
/// </summary>
/// <typeparam name="TKey">What can be sorted by: one value per header.</typeparam>
public sealed class ListOrder<TKey>
    where TKey : struct, Enum
{
    /// <summary>The header sorted by; null for the list's own order.</summary>
    public TKey? Key { get; private set; }

    public bool Descending { get; private set; }

    /// <summary>A click on a header: ascending, then descending, then the list's own order; another header starts ascending.</summary>
    public void Toggle(TKey key)
    {
        if (!Equals(Key, key))
        {
            (Key, Descending) = (key, false);
        }
        else if (!Descending)
        {
            Descending = true;
        }
        else
        {
            (Key, Descending) = (null, false);
        }
    }

    /// <summary>The header's arrow: ↑ ascending, ↓ descending, nothing otherwise.</summary>
    public string Mark(TKey key) => !Equals(Key, key) ? "" : Descending ? "↓" : "↑";

    /// <summary><c>aria-sort</c> of the header.</summary>
    public string AriaSort(TKey key) => !Equals(Key, key) ? "none" : Descending ? "descending" : "ascending";

    /// <summary>The rows in this order; <paramref name="valueOf"/> gives a row's value for a header, null for none.</summary>
    public IReadOnlyList<T> Apply<T>(IReadOnlyList<T> rows, Func<TKey, Func<T, IComparable?>> valueOf)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(valueOf);
        if (Key is not { } key)
        {
            return rows;
        }

        var value = valueOf(key);
        var keyed = rows.Select(row => (Row: row, Value: value(row))).ToList();
        var present = keyed.Where(r => r.Value is not null);
        var sorted = Descending
            ? present.OrderByDescending(r => r.Value, ValueComparer.Instance)
            : present.OrderBy(r => r.Value, ValueComparer.Instance);
        return [.. sorted.Concat(keyed.Where(r => r.Value is null)).Select(r => r.Row)];
    }

    private sealed class ValueComparer : IComparer<IComparable?>
    {
        public static readonly ValueComparer Instance = new();

        public int Compare(IComparable? x, IComparable? y) => x is string a && y is string b
            ? StringComparer.CurrentCultureIgnoreCase.Compare(a, b)
            : Comparer<IComparable?>.Default.Compare(x, y);
    }
}
