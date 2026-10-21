using FerretSharp.Core.Connections;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;
using FerretSharp.Core.Settings;
using FerretSharp.UI.State;
using Microsoft.Extensions.Time.Testing;

namespace FerretSharp.UI.Tests.State;

/// <summary>
/// The incoming jumps of the form's row (review 3.44.0, B1: one class for both form panels): found once the row stays,
/// counted at once or on hover, cancelled when another row is shown.
/// </summary>
public sealed class IncomingCountsTests
{
    private static readonly FkJump Jump = new(
        new ForeignKeyInfo("FK_AUFTRAG", new("APP", "AUFTRAG"), ["KUNDE_ID"], new("APP", "KUNDE"), ["ID"], FkSource.Declared),
        JumpDirection.Incoming, new("APP", "AUFTRAG"), [], null);

    private readonly FakeTimeProvider _time = new();
    private readonly List<FkJump> _counted = [];
    private FkCountMode _mode = FkCountMode.Immediately;
    private int _changed;

    private IncomingCounts Create()
    {
        var counts = new FkCounts((jump, _) =>
        {
            _counted.Add(jump);
            return Task.FromResult(new DbResult<long?>(3, null, false));
        }, () => { }, _time);
        return new IncomingCounts(counts, () => _mode, work => work(), () => _changed++, _time);
    }

    [Fact]
    public async Task The_jumps_are_found_and_counted_once_the_row_stays()
    {
        using var incoming = Create();

        var started = incoming.StartAsync(() => [Jump]);
        Assert.Empty(incoming.Jumps);
        _time.Advance(IncomingCounts.Delay);
        await started;

        Assert.Equal([Jump], incoming.Jumps);
        Assert.Equal(1, _changed);
        Assert.Equal([Jump], _counted);
        Assert.Equal(new FkCount.Done(3), incoming.Counts.Of(Jump));
    }

    [Fact]
    public async Task Another_row_before_the_delay_finds_and_counts_nothing_for_the_first()
    {
        using var incoming = Create();
        var first = incoming.StartAsync(() => throw new InvalidOperationException("not asked: another row came"));

        var second = incoming.StartAsync(() => null); // its block is not loaded yet
        await first;
        _time.Advance(IncomingCounts.Delay);
        await second;

        Assert.Empty(incoming.Jumps);
        Assert.Empty(_counted);
    }

    [Fact]
    public async Task On_hover_only_the_entry_under_the_mouse_is_counted()
    {
        _mode = FkCountMode.OnHover;
        using var incoming = Create();
        var started = incoming.StartAsync(() => [Jump]);
        _time.Advance(IncomingCounts.Delay);
        await started;
        Assert.Empty(_counted);

        var hover = incoming.HoverAsync(Jump);
        _time.Advance(FkCounts.HoverDelay);
        await hover;

        Assert.Equal([Jump], _counted);
    }

    [Fact]
    public async Task Cancelled_before_a_jump_nothing_is_counted_on_hover()
    {
        _mode = FkCountMode.OnHover;
        using var incoming = Create();
        var started = incoming.StartAsync(() => [Jump]);
        _time.Advance(IncomingCounts.Delay);
        await started;

        incoming.Cancel(); // the jump's new tab would wait for the counts

        await incoming.HoverAsync(Jump);
        Assert.Empty(_counted);
    }
}
