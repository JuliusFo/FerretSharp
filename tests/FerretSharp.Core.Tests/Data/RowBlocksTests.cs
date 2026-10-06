using FerretSharp.Core.Data;

namespace FerretSharp.Core.Tests.Data;

public sealed class RowBlocksTests
{
    private static IReadOnlyList<RowData> Block(int start, int count) =>
        Enumerable.Range(start, count).Select(i => new RowData(RowKey.None.Instance, [i])).ToList();

    [Fact]
    public void Rows_are_found_by_their_grid_index()
    {
        var blocks = new RowBlocks();
        blocks.Keep(0, Block(0, 500));
        blocks.Keep(500, Block(500, 120));

        Assert.Equal(0, blocks.At(0)!.Values[0]);
        Assert.Equal(499, blocks.At(499)!.Values[0]);
        Assert.Equal(619, blocks.At(619)!.Values[0]);
        Assert.Null(blocks.At(620));
        Assert.Null(blocks.At(-1));
    }

    [Fact]
    public void The_block_farthest_from_the_newest_goes_first()
    {
        var blocks = new RowBlocks();
        for (var i = 0; i < RowBlocks.MaxBlocks; i++)
        {
            blocks.Keep(i * 10, Block(i * 10, 10));
        }

        blocks.Keep(5, Block(1000, 1)); // a 41st block near the start: the last one (farthest away) is dropped
        Assert.Null(blocks.At((RowBlocks.MaxBlocks - 1) * 10));
        Assert.NotNull(blocks.At((RowBlocks.MaxBlocks - 2) * 10));
        Assert.Equal(0, blocks.At(0)!.Values[0]);
    }

    [Fact]
    public void Reading_a_block_again_replaces_it_and_clear_forgets_all()
    {
        var blocks = new RowBlocks();
        blocks.Keep(0, Block(0, 2));
        blocks.Keep(0, Block(7, 2));
        Assert.Equal(7, blocks.At(0)!.Values[0]);

        blocks.Clear();
        Assert.Null(blocks.At(0));
    }
}
