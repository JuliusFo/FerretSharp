using FerretSharp.Core.Connections;
using FerretSharp.Core.Data;
using FerretSharp.Core.Display;
using FerretSharp.Core.Schema;
using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>
/// R5b (review 3.31.0, B1): the names beside foreign key values of one grid, out of FerretGrid – the order of plans,
/// generations, the queue of lookups, a slow lookup tried again, a failing one switched off, and nothing after the dispose.
/// </summary>
public sealed class GridDisplayNamesTests
{
    private static ColumnInfo Col(string name, string type) => new(name, type, null, false, null, null, true, false, null, 1);

    private static readonly TableDetails Kunde = new(new TableSummary("APP", "KUNDE", TableKind.Table),
        [Col("ID", "NUMBER"), Col("NAME", "VARCHAR2")], ["ID"], [], false);

    private static readonly DisplayLink KundeLink =
        new(1, new ForeignKeyInfo("FK_KUNDE", new("APP", "AUFTRAG"), ["KUNDE_ID"], Kunde.Table.Ref, ["ID"], FkSource.Declared), Kunde, 0, [new DisplayPart(1)]);

    private static readonly DisplayLink OtherLink = KundeLink with { Parts = [new DisplayPart(0)] };

    /// <summary>AUFTRAG rows: ID, KUNDE_ID.</summary>
    private static object?[] Row(decimal kunde) => [1m, kunde];

    private readonly FakeGrid _grid = new();
    private readonly Queue<TaskCompletionSource<DbResult<IReadOnlyList<LookupRow>?>>> _answers = new();
    private readonly List<DisplayLookup> _asked = [];
    private Func<Task<IReadOnlyList<DisplayLink>>> _plan = () => Task.FromResult<IReadOnlyList<DisplayLink>>([KundeLink]);
    private int _plans;

    private GridDisplayNames Create() => new(_grid, () =>
        {
            _plans++;
            return _plan();
        },
        (lookup, _) =>
        {
            _asked.Add(lookup);
            // Continues inline: setting an answer runs the lookups on until they wait for the next one – no waiting in the tests.
            var answer = new TaskCompletionSource<DbResult<IReadOnlyList<LookupRow>?>>();
            _answers.Enqueue(answer);
            return answer.Task;
        },
        () => (_, _, value) => value as string,
        i => "COLUMN_" + i);

    private static DbResult<IReadOnlyList<LookupRow>?> Found(params (decimal Key, string Name)[] rows) => new(rows.Select(r => new LookupRow(r.Key, [r.Name])).ToList(), null, false);

    private async Task AnswerAsync(DbResult<IReadOnlyList<LookupRow>?> result)
    {
        _answers.Dequeue().SetResult(result);
        await _grid.SettledAsync();
    }

    [Fact]
    public async Task A_plan_looks_up_the_held_rows_and_redraws_the_ones_that_show_a_key()
    {
        _grid.Rows = [Row(4711m), Row(4712m)];
        var names = Create();

        await names.PlanAsync();
        await _grid.SettledAsync();
        await AnswerAsync(Found((4711m, "PetersPizza")));

        Assert.Equal([4711m, 4712m], Assert.Single(_asked).Keys);
        Assert.Equal("PetersPizza", names.Names.NameOf(1, 4711m)?.Text);
        Assert.True(names.Names.NameOf(1, 4712m)?.NotFound);
        Assert.Equal((1, 1, 1), (_grid.Relabels, _grid.RedrawsAll, _grid.Redraws));
    }

    [Fact]
    public async Task The_same_links_start_nothing_over_unless_forced()
    {
        var names = Create();
        await names.PlanAsync();
        var first = names.Names;

        await names.PlanAsync();
        Assert.Same(first, names.Names);

        await names.PlanAsync(force: true);
        Assert.NotSame(first, names.Names);
    }

    /// <summary>A model loaded while a plan read the schema: the plan that finishes last is not always the newest.</summary>
    [Fact]
    public async Task A_plan_finishing_after_a_newer_one_is_dropped()
    {
        var slow = new TaskCompletionSource<IReadOnlyList<DisplayLink>>();
        _plan = () => slow.Task;
        var names = Create();
        var older = names.PlanAsync();
        _plan = () => Task.FromResult<IReadOnlyList<DisplayLink>>([OtherLink]);

        await names.PlanAsync();
        slow.SetResult([KundeLink]);
        await older;

        Assert.Equal([OtherLink], names.Names.Links);
    }

    [Fact]
    public async Task A_lookup_of_an_older_generation_is_not_applied()
    {
        _grid.Rows = [Row(4711m)];
        var names = Create();
        await names.PlanAsync();
        await _grid.SettledAsync();

        names.Clear(); // the grid read from its first row again
        await AnswerAsync(Found((4711m, "PetersPizza")));

        Assert.Null(names.Names.NameOf(1, 4711m));
        Assert.Equal(0, _grid.Redraws);
    }

    [Fact]
    public async Task A_slow_lookup_is_tried_again_and_switched_off_after_too_many()
    {
        _grid.Rows = [Row(4711m)];
        var names = Create();
        await names.PlanAsync();
        await _grid.SettledAsync();

        for (var i = 0; i < 10 && _grid.Notices.Count == 0; i++)
        {
            await AnswerAsync(new DbResult<IReadOnlyList<LookupRow>?>(null, null, false)); // the time limit, no rows
        }

        Assert.True(_asked.Count > 1);
        Assert.False(names.Names.Shows(1));
        Assert.Contains("COLUMN_1", Assert.Single(_grid.Notices));
    }

    [Fact]
    public async Task A_failing_lookup_switches_the_column_off_and_says_so_once()
    {
        _grid.Rows = [Row(4711m), Row(4712m)];
        var names = Create();
        await names.PlanAsync();
        await _grid.SettledAsync();

        await AnswerAsync(new DbResult<IReadOnlyList<LookupRow>?>(null, new DatabaseException("ORA-00904: invalid identifier", "ORA-00904"), false));
        names.Queue([Row(4713m)]);
        await _grid.SettledAsync();

        Assert.False(names.Names.Shows(1));
        Assert.Contains("ORA-00904", Assert.Single(_grid.Notices));
        Assert.Single(_asked); // the column asks nothing more
        Assert.Equal(2, _grid.Relabels);
    }

    [Fact]
    public async Task A_cancelled_lookup_may_be_asked_for_again()
    {
        _grid.Rows = [Row(4711m)];
        var names = Create();
        await names.PlanAsync();
        await _grid.SettledAsync();

        await AnswerAsync(new DbResult<IReadOnlyList<LookupRow>?>(null, null, true));
        names.Queue([Row(4711m)]);
        await _grid.SettledAsync();

        Assert.Equal(2, _asked.Count);
    }

    /// <summary>R5b: a plan after the grid was gone still read the schema; a running lookup was cancelled, its token source never disposed.</summary>
    [Fact]
    public async Task After_the_dispose_nothing_is_planned_or_looked_up()
    {
        _grid.Rows = [Row(4711m)];
        var names = Create();
        await names.PlanAsync();
        await _grid.SettledAsync();

        await names.DisposeAsync();
        await AnswerAsync(Found((4711m, "PetersPizza")));
        await names.PlanAsync(force: true);
        names.Queue([Row(4712m)]);
        await _grid.SettledAsync();

        Assert.Equal(1, _plans);
        Assert.Single(_asked);
        Assert.Null(names.Names.NameOf(1, 4711m));
    }

    [Fact]
    public async Task A_grid_that_is_gone_plans_nothing()
    {
        _grid.IsAlive = false;

        await Create().PlanAsync();

        Assert.Equal(0, _plans);
    }

    // ---------- one level deeper (WP-65) ----------

    private static readonly TableDetails Staat = new(new TableSummary("APP", "STAAT", TableKind.Table),
        [Col("CODE", "VARCHAR2"), Col("NAME", "VARCHAR2")], ["CODE"], [], false);

    private static readonly TableDetails KundeMitStaat = new(new TableSummary("APP", "KUNDE", TableKind.Table),
        [Col("ID", "NUMBER"), Col("NAME", "VARCHAR2"), Col("STAAT", "VARCHAR2")], ["ID"], [], false);

    private static readonly DisplayLink DeepLink = new(1,
        new ForeignKeyInfo("FK_KUNDE", new("APP", "AUFTRAG"), ["KUNDE_ID"], KundeMitStaat.Table.Ref, ["ID"], FkSource.Declared), KundeMitStaat, 0,
        [new DisplayPart(1), new DisplayPart(2, new DisplayLink(2,
            new ForeignKeyInfo("FK_STAAT", KundeMitStaat.Table.Ref, ["STAAT"], Staat.Table.Ref, ["CODE"], FkSource.Declared), Staat, 0, [new DisplayPart(1)]))]);

    private static DbResult<IReadOnlyList<LookupRow>?> FoundRows(params LookupRow[] rows) => new(rows, null, false);

    [Fact]
    public async Task Names_one_level_deeper_are_looked_up_after_the_first_lookup_and_redrawn()
    {
        _plan = () => Task.FromResult<IReadOnlyList<DisplayLink>>([DeepLink]);
        _grid.Rows = [Row(4711m)];
        var names = Create();
        await names.PlanAsync();
        await _grid.SettledAsync();

        await AnswerAsync(FoundRows(new LookupRow(4711m, ["Peter", "DE"])));
        Assert.Equal("Peter - DE", names.Names.NameOf(1, 4711m)?.Text);
        await AnswerAsync(FoundRows(new LookupRow("DE", ["Deutschland"])));

        Assert.Equal([null, 1], _asked.Select(l => l.Part));
        Assert.Equal(["DE"], _asked[1].Keys);
        Assert.Equal("Peter - Deutschland", names.Names.NameOf(1, 4711m)?.Text);
        Assert.Equal(2, _grid.Redraws);
        Assert.Empty(_answers);
    }

    [Fact]
    public async Task A_failing_lookup_one_level_deeper_keeps_the_column_and_says_so()
    {
        _plan = () => Task.FromResult<IReadOnlyList<DisplayLink>>([DeepLink]);
        _grid.Rows = [Row(4711m)];
        var names = Create();
        await names.PlanAsync();
        await _grid.SettledAsync();
        await AnswerAsync(FoundRows(new LookupRow(4711m, ["Peter", "DE"])));
        var relabels = _grid.Relabels;

        await AnswerAsync(new(null, new DatabaseException("ORA-00942: Tabelle oder View nicht vorhanden", "ORA-00942"), false));

        Assert.True(names.Names.Shows(1));
        Assert.Equal("Peter - DE", names.Names.NameOf(1, 4711m)?.Text);
        Assert.Contains("STAAT", Assert.Single(_grid.Notices));
        Assert.Equal(relabels, _grid.Relabels);
    }

    private sealed class FakeGrid : IDisplayNamesGrid
    {
        private readonly List<Task> _work = [];

        public bool IsAlive { get; set; } = true;

        public List<object?[]> Rows { get; set; } = [];

        public List<string> Notices { get; } = [];

        public int Relabels { get; private set; }

        public int RedrawsAll { get; private set; }

        public int Redraws { get; private set; }

        public IEnumerable<IReadOnlyList<object?>> HeldRows() => Rows;

        public Task RelabelAsync()
        {
            Relabels++;
            return Task.CompletedTask;
        }

        public Task RedrawAllAsync()
        {
            RedrawsAll++;
            return Task.CompletedTask;
        }

        public Task RedrawAsync(Func<IReadOnlyList<object?>, bool> touches)
        {
            if (Rows.Any(touches))
            {
                Redraws++;
            }

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
