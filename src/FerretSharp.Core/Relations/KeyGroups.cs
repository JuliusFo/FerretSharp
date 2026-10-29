using FerretSharp.Core.Data;

namespace FerretSharp.Core.Relations;

/// <summary>
/// Rows grouped by a key they share – the values a relationship or a unique key compares – with the keys to read in the order
/// they first came, and the rows a key read back belongs to: matched as <see cref="RowTraversal.KeyText"/>, CHAR values without
/// their padding where a column is fixed (review 3.54.0, B4: the traversal and the copy's check for existing rows each had a
/// copy).
/// </summary>
/// <param name="trim">A column on either side is CHAR: values are bound and compared without trailing blanks.</param>
internal sealed class KeyGroups(bool trim)
{
    private readonly Dictionary<string, List<RowData>> _rows = new(StringComparer.Ordinal);
    private readonly List<IReadOnlyList<object>> _keys = [];

    /// <summary>The keys to read, each once, as bound.</summary>
    public IReadOnlyList<IReadOnlyList<object>> Keys => _keys;

    /// <summary>A value as bound and compared.</summary>
    public object Bound(object value) => trim && value is string text ? text.TrimEnd(' ') : value;

    /// <summary>Adds <paramref name="row"/> under its key (no NULL in it).</summary>
    public void Add(IEnumerable<object> key, RowData row)
    {
        object[] bound = [.. key.Select(Bound)];
        var text = RowTraversal.KeyText(bound, trim);
        if (!_rows.TryGetValue(text, out var list))
        {
            _rows[text] = list = [];
            _keys.Add(bound);
        }

        list.Add(row);
    }

    /// <summary>The rows added under the key <paramref name="key"/> (a key as given to <see cref="Keys"/> or read back); none if unknown.</summary>
    public IReadOnlyList<RowData> RowsOf(IEnumerable<object?> key) =>
        _rows.GetValueOrDefault(RowTraversal.KeyText([.. key.Select(v => Bound(v!))], trim)) ?? [];
}
