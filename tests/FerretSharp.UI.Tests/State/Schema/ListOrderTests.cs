using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>A list view's rows sorted by a header click (columns, usage of the columns).</summary>
public sealed class ListOrderTests
{
    private enum Key { Name, Count, Comment }

    private sealed record Item(string Name, int Count, string? Comment);

    private static readonly IReadOnlyList<Item> Items =
    [
        new("KUNDE_ID", 3, "Key"),
        new("name", 0, null),
        new("ÄNDERUNG", 3, "changed"),
        new("Bemerkung", 1, null),
    ];

    private static Func<Item, IComparable?> ValueOf(Key key) => key switch
    {
        Key.Name => i => i.Name,
        Key.Count => i => i.Count,
        _ => i => i.Comment,
    };

    private static IReadOnlyList<string> Names(ListOrder<Key> order) => [.. order.Apply(Items, ValueOf).Select(i => i.Name)];

    [Fact]
    public void A_click_sorts_ascending_then_descending_then_back_to_the_list_s_order()
    {
        var order = new ListOrder<Key>();
        Assert.Same(Items, order.Apply(Items, ValueOf));

        order.Toggle(Key.Name);
        Assert.Equal(["ÄNDERUNG", "Bemerkung", "KUNDE_ID", "name"], Names(order)); // the UI's culture, without case
        Assert.Equal(("↑", "ascending"), (order.Mark(Key.Name), order.AriaSort(Key.Name)));

        order.Toggle(Key.Name);
        Assert.Equal(["name", "KUNDE_ID", "Bemerkung", "ÄNDERUNG"], Names(order));
        Assert.Equal(("↓", "descending"), (order.Mark(Key.Name), order.AriaSort(Key.Name)));

        order.Toggle(Key.Name);
        Assert.Null(order.Key);
        Assert.Equal(("", "none"), (order.Mark(Key.Name), order.AriaSort(Key.Name)));
    }

    [Fact]
    public void Another_header_starts_ascending_and_equal_rows_keep_the_list_s_order()
    {
        var order = new ListOrder<Key>();
        order.Toggle(Key.Name);
        order.Toggle(Key.Name);

        order.Toggle(Key.Count);
        Assert.Equal(["name", "Bemerkung", "KUNDE_ID", "ÄNDERUNG"], Names(order));
        Assert.Equal("", order.Mark(Key.Name));

        order.Toggle(Key.Count);
        Assert.Equal(["KUNDE_ID", "ÄNDERUNG", "Bemerkung", "name"], Names(order));
    }

    /// <summary>A column without comment says nothing about the order: last in both directions.</summary>
    [Fact]
    public void Rows_without_a_value_stay_last()
    {
        var order = new ListOrder<Key>();

        order.Toggle(Key.Comment);
        Assert.Equal(["ÄNDERUNG", "KUNDE_ID", "name", "Bemerkung"], Names(order));

        order.Toggle(Key.Comment);
        Assert.Equal(["KUNDE_ID", "ÄNDERUNG", "name", "Bemerkung"], Names(order));
    }
}
