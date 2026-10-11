using FerretSharp.Core.Data;
using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>R4b: the bookkeeping of undo and redo for grid writes, out of <see cref="WorkspaceEditing"/>.</summary>
public sealed class GridWriteHistoryTests
{
    private static readonly Guid Workspace = Guid.NewGuid();
    private static readonly Guid Other = Guid.NewGuid();

    private readonly GridWriteHistory _history = new();
    private readonly TableTab _tab = new(Workspace, TestApp.Kunden);

    private static WriteAction Statement(Guid? origin = null) =>
        new(Guid.NewGuid(), WriteActionKind.Statement, "UPDATE KUNDEN", 1, DateTimeOffset.Now) { RedoOf = origin };

    [Fact]
    public void Taking_a_grid_write_back_moves_it_to_the_undone_ones_and_a_redo_takes_it_out_again()
    {
        var (first, second) = (Guid.NewGuid(), Guid.NewGuid());
        var batch = new FlushBatch();
        _history.Written(Workspace, first, _tab, batch);
        _history.Written(Workspace, second, _tab, new FlushBatch());

        Assert.Null(_history.TakeBack(Workspace, Guid.NewGuid())); // a statement: nothing of the grid
        var taken = _history.TakeBack(Workspace, first);

        Assert.Equal(new GridWrite(first, _tab, batch), taken);
        Assert.Same(taken, _history.Undone(Workspace, first));
        Assert.Same(taken, _history.Find(Workspace, first)); // still known while taken back
        Assert.Equal([second], _history.TakeAll(Workspace).Select(w => w.ActionId));

        Assert.Same(taken, _history.StartRedo(Workspace, first));
        Assert.Null(_history.Undone(Workspace, first));
        Assert.Null(_history.StartRedo(Workspace, first));

        _history.RedoStopped(Workspace, taken!); // the problem dialog decides: still taken back
        Assert.Same(taken, _history.Undone(Workspace, first));
    }

    [Fact]
    public void A_transaction_that_ended_without_commit_gives_back_every_grid_write_newest_first()
    {
        var ids = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToList();
        ids.ForEach(id => _history.Written(Workspace, id, _tab, new FlushBatch()));
        _history.Written(Other, Guid.NewGuid(), _tab, new FlushBatch());

        Assert.Equal(Enumerable.Reverse(ids), _history.TakeAll(Workspace).Select(w => w.ActionId));
        Assert.Empty(_history.TakeAll(Workspace));
        Assert.Single(_history.TakeAll(Other)); // other workspaces keep theirs
    }

    [Fact]
    public void Only_writes_that_can_still_be_redone_stay_known()
    {
        var (kept, ended) = (Guid.NewGuid(), Guid.NewGuid());
        _history.Written(Workspace, kept, _tab, new FlushBatch());
        _history.Written(Workspace, ended, _tab, new FlushBatch());
        _history.TakeBack(Workspace, kept);
        _history.TakeBack(Workspace, ended);

        _history.KeepRedoable(Workspace, [kept]);

        Assert.NotNull(_history.Undone(Workspace, kept));
        Assert.Null(_history.Undone(Workspace, ended));
    }

    [Fact]
    public void A_statement_s_tab_is_found_also_for_its_redo()
    {
        var sql = new SqlTab(Workspace, "SQL 1");
        var first = Statement();
        _history.NoteStatement(sql, first);

        Assert.Same(sql, _history.StatementTab(Workspace, first));
        Assert.Same(sql, _history.StatementTab(Workspace, Statement(origin: first.Id)));
        Assert.Null(_history.StatementTab(Other, first));
    }

    [Fact]
    public void Forgetting_a_workspace_drops_all_of_it()
    {
        var id = Guid.NewGuid();
        _history.Written(Workspace, id, _tab, new FlushBatch());
        _history.TakeBack(Workspace, id);
        _history.NoteStatement(new SqlTab(Workspace, "SQL 1"), Statement());
        _history.Written(Other, Guid.NewGuid(), _tab, new FlushBatch());

        _history.Forget(Workspace);

        Assert.False(_history.Knows(Workspace));
        Assert.Null(_history.Find(Workspace, id));
        Assert.True(_history.Knows(Other));
    }
}
