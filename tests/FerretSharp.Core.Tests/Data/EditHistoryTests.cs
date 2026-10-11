using FerretSharp.Core.Data;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Tests.Data;

/// <summary>R4b: the history of pending edits, out of <see cref="ChangeTracker"/> (the tracker's own undo/redo tests stay).</summary>
public sealed class EditHistoryTests
{
    private static readonly TableDetails Kunde = new(new TableSummary("SHOP", "KUNDE", TableKind.Table),
        [new ColumnInfo("ID", "NUMBER", null, false, 10, 0, false, false, null, 1), new ColumnInfo("NAME", "VARCHAR2", 20, true, null, null, true, false, null, 2)],
        ["ID"], [], false);

    private static RowState With(RowChange change, string name) => new(change, new Dictionary<int, object?> { [1] = name }, null, -1);

    private readonly RowChange _row = new ChangeTracker(Kunde).AddRow();

    [Fact]
    public void Undo_hands_back_the_state_before_and_redo_the_state_after()
    {
        var history = new EditHistory();
        var state = With(_row, "Meier");
        history.Remember([RowState.Untouched(_row)], _ => state);

        Assert.True(history.CanUndo);
        Assert.Equal([-1], history.Undo().Select(s => s.NewRowIndex));
        Assert.True(history.CanRedo);
        Assert.Same(state, Assert.Single(history.Redo()));
        Assert.False(history.CanRedo);
    }

    [Fact]
    public void An_edit_that_changed_nothing_is_no_step_and_a_new_edit_ends_redo()
    {
        var history = new EditHistory();
        history.Remember([With(_row, "Meier")], _ => With(_row, "Meier")); // the same value: nothing to take back
        Assert.False(history.CanUndo);

        history.Remember([With(_row, "Meier")], _ => With(_row, "Müller"));
        history.Undo();
        Assert.True(history.CanRedo);

        history.Remember([With(_row, "Meier")], _ => With(_row, "Schulz"));
        Assert.False(history.CanRedo);
        Assert.Empty(history.Redo());
    }

    [Fact]
    public void Only_the_newest_steps_are_kept_and_clearing_ends_the_history()
    {
        var history = new EditHistory();
        for (var i = 0; i <= EditHistory.MaxSteps; i++)
        {
            var name = "N" + i;
            history.Remember([RowState.Untouched(_row)], _ => With(_row, name));
        }

        var undone = 0;
        while (history.Undo().Count > 0)
        {
            undone++;
        }

        Assert.Equal(EditHistory.MaxSteps, undone);

        history.Clear();
        Assert.False(history.CanUndo || history.CanRedo);
    }

    [Fact]
    public void States_are_the_same_by_value_delete_mark_and_place()
    {
        Assert.True(With(_row, "a").Same(With(_row, "a")));
        Assert.False(With(_row, "a").Same(With(_row, "b")));
        Assert.False(With(_row, "a").Same(With(_row, "a") with { Deleted = ChangeStage.Pending }));
        Assert.False(With(_row, "a").Same(With(_row, "a") with { NewRowIndex = 0 }));
    }
}
