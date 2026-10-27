using FerretSharp.Core.Audit;
using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Connections;
using FerretSharp.Core.Data;
using FerretSharp.Core.Schema;
using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>WP-63: an audit table's grid asks for the changes of each block it reads and redraws the rows they mark.</summary>
public sealed class GridAuditChangesTests
{
    private static ColumnInfo Col(string name, string type) => new(name, type, null, false, null, null, true, false, null, 1);

    /// <summary>AUDIT_ID, AUDIT_TRANS, KUNDE_ID, NAME – no key of its own: rows by ROWID.</summary>
    private static readonly TableDetails AuditKunde = new(new TableSummary("APP", "AUDIT_KUNDE", TableKind.Table),
        [Col("AUDIT_ID", "NUMBER"), Col("AUDIT_TRANS", "CHAR"), Col("KUNDE_ID", "NUMBER"), Col("NAME", "VARCHAR2")], [], [], false);

    private static readonly AuditView View = new(new TableRef("APP", "KUNDE"), new AuditVersions([2], 0, [3]), 1, new AuditAction("AUDIT_TRANS", "I", "U", "D"));

    private static RowData Row(string rowId, string action, decimal kunde) => new(new RowKey.RowId(rowId), [1m, action, kunde, "x"]);

    private readonly FakeGrid _grid = new();
    private readonly Queue<TaskCompletionSource<DbResult<IReadOnlyList<AuditRowChange>>>> _answers = new();
    private readonly List<IReadOnlyList<RowData>> _asked = [];

    private GridAuditChanges Create(AuditView? view = null) => new(_grid, view ?? View, (_, rows, _) =>
    {
        _asked.Add(rows);
        var answer = new TaskCompletionSource<DbResult<IReadOnlyList<AuditRowChange>>>();
        _answers.Enqueue(answer);
        return answer.Task;
    });

    private static DbResult<IReadOnlyList<AuditRowChange>> Changes(params AuditRowChange[] changes) => new(changes, null, false);

    private static AuditRowChange Changed(string rowId, params int[] columns) => new(new RowKey.RowId(rowId), false, columns.ToHashSet());

    private Task AnswerAsync(DbResult<IReadOnlyList<AuditRowChange>> result)
    {
        _answers.Dequeue().SetResult(result);
        return _grid.SettledAsync();
    }

    [Fact]
    public async Task A_block_asks_once_and_redraws_the_rows_with_changes()
    {
        var audit = Create();
        IReadOnlyList<RowData> block = [Row("A", "I", 1m), Row("B", "U", 1m)];

        audit.Queue(block);
        await AnswerAsync(Changes(new AuditRowChange(new RowKey.RowId("A"), true, new HashSet<int>()), Changed("B", 3)));

        Assert.Same(block, Assert.Single(_asked));
        Assert.Equal([new RowKey.RowId("B")], Assert.Single(_grid.Redrawn));
        Assert.Equal([3], audit.MarksOf(new RowKey.RowId("B"), block[1].Values)!.Changed);
        Assert.Equal(AuditActionKind.Update, audit.MarksOf(new RowKey.RowId("B"), block[1].Values)!.Action);
    }

    [Fact]
    public void The_action_shows_at_once_from_the_row_itself()
    {
        var audit = Create();

        Assert.Equal(new AuditMarks(new HashSet<int>(), AuditActionKind.Delete).Action, audit.MarksOf(new RowKey.RowId("A"), Row("A", "D", 1m).Values)!.Action);
        Assert.Empty(audit.MarksOf(new RowKey.RowId("A"), Row("A", "D", 1m).Values)!.Changed);
        Assert.Null(audit.MarksOf(new RowKey.RowId("A"), Row("A", "?", 1m).Values)); // nothing marks it
    }

    [Fact]
    public async Task Blocks_wait_for_each_other_and_an_answer_after_reading_anew_is_dropped()
    {
        var audit = Create();
        audit.Queue([Row("A", "U", 1m)]);
        audit.Queue([Row("B", "U", 2m)]);
        Assert.Single(_asked); // one query at a time on the workspace's session

        audit.Clear(); // F5: the queued block is forgotten, the running answer comes too late
        await AnswerAsync(Changes(Changed("A", 3)));

        Assert.Single(_asked);
        Assert.Empty(_grid.Redrawn);
        Assert.Null(audit.MarksOf(new RowKey.RowId("A"), null));
    }

    [Fact]
    public async Task A_failed_query_switches_the_highlighting_off_once()
    {
        var audit = Create();
        audit.Queue([Row("A", "U", 1m)]);
        audit.Queue([Row("B", "U", 2m)]);

        await AnswerAsync(new(null, new DatabaseException("ORA-00932: inconsistent datatypes"), false));
        audit.Queue([Row("C", "U", 3m)]);

        Assert.Single(_asked);
        Assert.False(audit.Compares);
        Assert.Contains("ORA-00932", Assert.Single(_grid.Notices));
        Assert.Equal(AuditActionKind.Update, audit.MarksOf(new RowKey.RowId("C"), Row("C", "U", 3m).Values)!.Action); // the action stays
    }

    [Fact]
    public void Without_version_order_nothing_is_asked()
    {
        var audit = Create(View with { Versions = null });

        audit.Queue([Row("A", "U", 1m)]);

        Assert.Empty(_asked);
        Assert.False(audit.Compares);
    }

    [Fact]
    public async Task Nothing_is_asked_or_drawn_after_the_dispose()
    {
        var audit = Create();
        audit.Queue([Row("A", "U", 1m)]);

        await audit.DisposeAsync();
        await AnswerAsync(Changes(Changed("A", 3)));
        audit.Queue([Row("B", "U", 2m)]);

        Assert.Single(_asked);
        Assert.Empty(_grid.Redrawn);
    }

    [Fact]
    public void Grid_rows_carry_the_changed_cells_and_the_action()
    {
        var presentation = TablePresentation.Plain(AuditKunde);
        var row = Row("A", "D", 1m);

        var marked = GridRows.Build(presentation, null, row.Values, audit: new AuditMarks(new HashSet<int> { 3, 2 }, AuditActionKind.Delete));
        var update = GridRows.Build(presentation, null, row.Values, audit: new AuditMarks(new HashSet<int>(), AuditActionKind.Update));
        var plain = GridRows.Build(presentation, null, row.Values);

        Assert.Equal(["c2", "c3"], (IEnumerable<string>)marked["__c"]!);
        Assert.Equal("d", marked["__a"]);
        Assert.False(update.ContainsKey("__c"));
        Assert.False(update.ContainsKey("__a")); // an update shows by its cells alone
        Assert.False(plain.ContainsKey("__c") || plain.ContainsKey("__a"));
    }

    private sealed class FakeGrid : IAuditChangesGrid
    {
        private readonly List<Task> _work = [];

        public bool IsAlive { get; set; } = true;

        public List<IReadOnlySet<RowKey>> Redrawn { get; } = [];

        public List<string> Notices { get; } = [];

        public Task RedrawAsync(IReadOnlySet<RowKey> keys)
        {
            Redrawn.Add(keys);
            return Task.CompletedTask;
        }

        public void Fire(Func<Task> work) => _work.Add(work());

        public void Notify(string text) => Notices.Add(text);

        /// <summary>The fired work runs inline up to the next answer it waits for; a failure is thrown here.</summary>
        public async Task SettledAsync()
        {
            foreach (var task in _work.Where(t => t.IsFaulted))
            {
                await task;
            }
        }
    }
}
