namespace FerretSharp.UI.State;

/// <summary>
/// Rows picked in a list view that is no grid (the columns view's rows for their script): a click picks one row, Ctrl+click
/// adds or takes away one, Shift+click the rows between the last clicked one and this one in the order shown (with Ctrl
/// added to what is picked). A right-click outside the picked rows picks that row alone, inside them keeps them – as in the
/// Explorer of Windows. Kept while the view is mounted, not saved.
/// </summary>
public sealed class ListSelection
{
    private readonly HashSet<string> _picked = new(StringComparer.Ordinal);

    /// <summary>The row Shift+click counts from: the last one clicked without Shift.</summary>
    private string? _anchor;

    public int Count => _picked.Count;

    public bool Contains(string key) => _picked.Contains(key);

    /// <param name="key">The clicked row.</param>
    /// <param name="shown">The rows in the order shown (filtered, sorted).</param>
    public void Click(string key, IReadOnlyList<string> shown, bool ctrl, bool shift)
    {
        ArgumentNullException.ThrowIfNull(shown);
        var from = _anchor is null ? -1 : IndexOf(shown, _anchor);
        var to = IndexOf(shown, key);
        if (shift && from >= 0 && to >= 0)
        {
            if (!ctrl)
            {
                _picked.Clear();
            }

            for (var i = Math.Min(from, to); i <= Math.Max(from, to); i++)
            {
                _picked.Add(shown[i]);
            }

            return;
        }

        if (ctrl)
        {
            if (!_picked.Remove(key))
            {
                _picked.Add(key);
            }
        }
        else
        {
            _picked.Clear();
            _picked.Add(key);
        }

        _anchor = key;
    }

    /// <summary>A right-click on a row: outside the picked rows it picks that row alone, inside them nothing changes.</summary>
    public void ContextClick(string key)
    {
        if (!_picked.Contains(key))
        {
            _picked.Clear();
            _picked.Add(key);
            _anchor = key;
        }
    }

    public void Clear()
    {
        _picked.Clear();
        _anchor = null;
    }

    /// <summary>The picked rows among <paramref name="shown"/>, in its order: rows a filter hides are left out, as the user cannot see them.</summary>
    public IReadOnlyList<string> In(IEnumerable<string> shown) => shown.Where(_picked.Contains).ToList();

    private static int IndexOf(IReadOnlyList<string> shown, string key)
    {
        for (var i = 0; i < shown.Count; i++)
        {
            if (string.Equals(shown[i], key, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }
}
