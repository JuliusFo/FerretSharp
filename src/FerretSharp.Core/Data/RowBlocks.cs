namespace FerretSharp.Core.Data;

/// <summary>
/// The raw rows behind a grid that loads in blocks (AG Grid's infinite row model): kept as long as the grid keeps the
/// block, so copying, the context menu and editing work on the values, not the display texts. When more than
/// <see cref="MaxBlocks"/> are kept, the block farthest from the newest one goes – like AG Grid does (R2: was in both grids).
/// </summary>
public sealed class RowBlocks
{
    /// <summary>maxBlocksInCache in grid.js.</summary>
    public const int MaxBlocks = 40;

    private readonly Dictionary<int, IReadOnlyList<RowData>> _blocks = [];

    /// <summary>Keeps the rows of the block starting at <paramref name="startRow"/> (replacing an older read of it).</summary>
    public void Keep(int startRow, IReadOnlyList<RowData> rows)
    {
        _blocks[startRow] = rows;
        while (_blocks.Count > MaxBlocks)
        {
            _blocks.Remove(_blocks.Keys.MaxBy(start => Math.Abs(start - startRow)));
        }
    }

    /// <summary>The row with this grid index; null if its block is no longer (or not yet) kept.</summary>
    public RowData? At(int index)
    {
        foreach (var (start, rows) in _blocks)
        {
            if (index >= start && index < start + rows.Count)
            {
                return rows[index - start];
            }
        }

        return null;
    }

    /// <summary>A new query (filter, sort order): the kept blocks are stale.</summary>
    public void Clear() => _blocks.Clear();
}
