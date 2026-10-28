using FerretSharp.Core.Connections;

namespace FerretSharp.Core.Tests.Connections;

/// <summary>WP-66: order and collapsed groups, shared by the switcher and the connections page.</summary>
public sealed class ConnectionGroupStateTests : IDisposable
{
    private readonly TestFolder _folder = new();

    private string FilePath => _folder.Combine("connection-groups.json");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _folder.Dispose();

    private async Task<ConnectionGroupState> LoadedAsync()
    {
        var state = new ConnectionGroupState(FilePath);
        await state.LoadAsync(Ct);
        return state;
    }

    [Fact]
    public async Task Missing_or_corrupt_file_means_all_expanded_without_order()
    {
        var state = await LoadedAsync();
        Assert.Empty(state.Order);
        Assert.False(state.IsCollapsed("ERP"));

        await File.WriteAllTextAsync(FilePath, "garbage", Ct);
        await state.LoadAsync(Ct);
        Assert.Empty(state.Order);
    }

    [Fact]
    public async Task Collapsed_groups_and_the_order_survive_a_restart()
    {
        var state = await LoadedAsync();
        await state.SetCollapsedAsync(" erp ", true, Ct);
        await state.SetCollapsedAsync(null, true, Ct);
        await state.SetOrderAsync(["Kasse", "ERP", "kasse", " "], Ct);

        var loaded = new ConnectionGroupState(FilePath);
        var changed = 0;
        loaded.Changed += () => changed++;
        await loaded.LoadAsync(Ct);

        Assert.Equal(1, changed); // the lists rendered before the file was read
        Assert.True(loaded.IsCollapsed("ERP"));
        Assert.True(loaded.IsCollapsed(null)); // connections without a group
        Assert.False(loaded.IsCollapsed("Kasse"));
        Assert.Equal(["Kasse", "ERP"], loaded.Order);

        await loaded.SetCollapsedAsync("Erp", false, Ct);
        Assert.False(loaded.IsCollapsed("ERP"));
    }

    [Theory]
    [InlineData("""{ "version": 1 }""", 0)]
    [InlineData("""{ "version": 1, "order": [ null, "ERP", "erp", "  " ], "collapsed": [ null ] }""", 1)]
    public async Task Files_edited_by_hand_are_read_as_far_as_they_go(string content, int ordered)
    {
        await File.WriteAllTextAsync(FilePath, content, Ct);

        var state = await LoadedAsync();

        Assert.Equal(ordered, state.Order.Count);
        Assert.False(state.IsCollapsed(null));
    }

    [Fact]
    public async Task A_file_of_a_newer_version_is_not_overwritten()
    {
        const string Newer = """{ "version": 2, "order": [ "ERP" ], "colors": { "ERP": "red" } }""";
        await File.WriteAllTextAsync(FilePath, Newer, Ct);
        var state = await LoadedAsync();

        Assert.True(await state.SetCollapsedAsync("ERP", true, Ct));

        Assert.True(state.IsCollapsed("ERP")); // in memory for this session
        Assert.Equal(Newer, await File.ReadAllTextAsync(FilePath, Ct));
    }

    [Fact]
    public async Task A_renamed_group_keeps_its_place_and_whether_it_is_collapsed()
    {
        var state = await LoadedAsync();
        await state.SetOrderAsync(["Kasse", "ERP"], Ct);
        await state.SetCollapsedAsync("ERP", true, Ct);

        await state.RenameAsync("erp", "Warenwirtschaft", merges: false, Ct);

        Assert.Equal(["Kasse", "Warenwirtschaft"], state.Order);
        Assert.True(state.IsCollapsed("Warenwirtschaft"));
        Assert.False(state.IsCollapsed("ERP"));
    }

    [Fact]
    public async Task Merged_into_another_group_the_target_keeps_its_place_and_is_expanded()
    {
        var state = await LoadedAsync();
        await state.SetOrderAsync(["ERP", "Kasse"], Ct);
        await state.SetCollapsedAsync("ERP", true, Ct);
        await state.SetCollapsedAsync("Kasse", true, Ct);

        await state.RenameAsync("ERP", "Kasse", merges: true, Ct);

        Assert.Equal(["Kasse"], state.Order);
        Assert.False(state.IsCollapsed("Kasse"));
    }

    [Fact]
    public void Moving_puts_a_group_before_another_or_at_the_end()
    {
        string[] shown = ["A", "B", "C", "D"];

        Assert.Equal(["C", "A", "B", "D"], ConnectionGroupState.Move(shown, "c", "A"));
        Assert.Equal(["B", "C", "D", "A"], ConnectionGroupState.Move(shown, "A", null));
        Assert.Equal(["A", "C", "B", "D"], ConnectionGroupState.Move(shown, "B", "D"));
        Assert.Same(shown, ConnectionGroupState.Move(shown, "B", "B"));
        Assert.Same(shown, ConnectionGroupState.Move(shown, "X", "A"));
        Assert.Same(shown, ConnectionGroupState.Move(shown, "A", "X"));
    }

    [Fact]
    public async Task A_file_that_cannot_be_written_keeps_the_change_for_the_session()
    {
        Directory.CreateDirectory(FilePath); // a folder where the file should be
        var state = await LoadedAsync();

        Assert.False(await state.SetCollapsedAsync("ERP", true, Ct));
        Assert.True(state.IsCollapsed("ERP"));
    }

    /// <summary>Review of WP-66: two quick clicks each started from the old state, and the second undid the first.</summary>
    [Fact]
    public async Task Quick_changes_one_after_another_all_count()
    {
        var state = await LoadedAsync();

        await Task.WhenAll(
            state.ToggleCollapsedAsync("A", Ct),
            state.ToggleCollapsedAsync("B", Ct),
            state.SetOrderAsync(order => [.. order, "A"], Ct),
            state.SetOrderAsync(order => [.. order, "B"], Ct));

        var loaded = await LoadedAsync();
        Assert.True(loaded.IsCollapsed("A"));
        Assert.True(loaded.IsCollapsed("B"));
        Assert.Equal(["A", "B"], loaded.Order);
    }

    /// <summary>Review of WP-66: a new group of a name an old one had came up collapsed, and the moved connection was hidden.</summary>
    [Fact]
    public async Task A_new_name_drops_what_an_earlier_group_of_it_left()
    {
        var state = await LoadedAsync();
        await state.SetOrderAsync(["X", "A"], Ct);
        await state.SetCollapsedAsync("X", true, Ct);

        await state.RenameAsync("A", "x", merges: false, Ct);

        Assert.Equal(["x"], state.Order); // the place of A
        Assert.False(state.IsCollapsed("x"));

        await state.SetCollapsedAsync("Y", true, Ct);
        await state.ForgetAsync("y", Ct);
        Assert.False(state.IsCollapsed("Y"));
    }
}
