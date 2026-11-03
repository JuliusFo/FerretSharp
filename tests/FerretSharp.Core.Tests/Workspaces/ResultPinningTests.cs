using FerretSharp.Core.Workspaces;

namespace FerretSharp.Core.Tests.Workspaces;

/// <summary>WP-73: pinned columns of a query result, by name and – for names the result has twice – by occurrence.</summary>
public sealed class ResultPinningTests
{
    // SELECT a.ID, a.NAME, k.ID, k.ORT FROM … : two columns named ID.
    private static readonly string[] Join = ["ID", "NAME", "ID", "ORT"];

    [Fact]
    public void The_second_column_of_a_name_is_pinned_alone_and_comes_first_in_pin_order()
    {
        var pins = ResultPinning.Toggle([], Join, 3);
        pins = ResultPinning.Toggle(pins, Join, 2);

        Assert.Equal([new ResultColumnPin("ORT"), new ResultColumnPin("ID", 1)], pins);
        Assert.False(ResultPinning.IsPinned(pins, Join, 0));
        Assert.Equal([3, 2, 0, 1], ResultPinning.DisplayOrder(pins, Join));
        Assert.Equal([new ResultColumnPin("ORT")], ResultPinning.Toggle(pins, Join, 2));
    }

    /// <summary>A script's results share the tab's pins: dragging in one result keeps the pins of the others' columns.</summary>
    [Fact]
    public void Dragging_replaces_only_the_pins_of_the_shown_result()
    {
        IReadOnlyList<ResultColumnPin> pins = [new("BETRAG"), new("ORT")]; // BETRAG: a column of another result of the script

        var dragged = ResultPinning.FromGrid(pins, Join, [1, 3]);

        Assert.Equal([new ResultColumnPin("BETRAG"), new ResultColumnPin("NAME"), new ResultColumnPin("ORT")], dragged);
        Assert.Equal([1, 3], ResultPinning.Pinned(dragged, Join));
        Assert.Equal([new ResultColumnPin("BETRAG")], ResultPinning.FromGrid(dragged, Join, []));
    }

    [Fact]
    public void At_most_the_newest_pins_are_kept()
    {
        var names = Enumerable.Range(0, ResultPinning.MaxPins + 5).Select(i => $"C{i}").ToArray();

        var pins = names.Select((_, i) => i).Aggregate((IReadOnlyList<ResultColumnPin>)[], (p, i) => ResultPinning.Toggle(p, names, i));

        Assert.Equal(ResultPinning.MaxPins, pins.Count);
        Assert.Equal(new ResultColumnPin("C5"), pins[0]);
        Assert.Equal(new ResultColumnPin($"C{names.Length - 1}"), pins[^1]);
    }
}
