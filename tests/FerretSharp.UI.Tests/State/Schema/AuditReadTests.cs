using FerretSharp.Core.Audit;
using FerretSharp.Core.Schema;
using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>
/// Code review 2026-10-28: "Reload" and the reload after a run that stopped halfway read at the same time – the earlier
/// one finishing last left the state from before the run, and its end freed "Reload" while the later one still read.
/// </summary>
public sealed class AuditReadTests
{
    private static readonly AuditState WithAuditTable = new(new TableDetails(new TableSummary("SHOP", "AUDIT_KUNDE", TableKind.Table), [], [], [], false), null);

    [Fact]
    public void An_earlier_read_finishing_last_changes_nothing()
    {
        var read = new AuditRead();
        var reload = read.Begin();
        var afterRun = read.Begin();

        Assert.True(read.End(afterRun, succeeded: true, WithAuditTable));
        Assert.False(read.End(reload, succeeded: true, AuditState.None));

        Assert.Same(WithAuditTable, read.State);
        Assert.False(read.Reading);
    }

    [Fact]
    public void An_earlier_read_finishing_first_leaves_the_later_one_reading()
    {
        var read = new AuditRead();
        var reload = read.Begin();
        var afterRun = read.Begin();

        Assert.False(read.End(reload, succeeded: false, null));

        Assert.True(read.Reading);
        Assert.Null(read.State);
        Assert.False(read.Unread);
    }

    [Fact]
    public void A_failed_read_stays_unread_while_reading_again()
    {
        var read = new AuditRead();
        read.End(read.Begin(), succeeded: false, null);
        Assert.True(read.Unread);

        var again = read.Begin();
        Assert.True(read.Unread);
        Assert.True(read.Reading);

        read.End(again, succeeded: true, null);
        Assert.False(read.Unread);
        Assert.Same(AuditState.None, read.State);
    }

    [Fact]
    public void Without_a_template_nothing_is_known_and_a_running_read_is_dropped()
    {
        var read = new AuditRead();
        Assert.False(read.Unread); // nothing read yet: a new table

        var running = read.Begin();
        read.WithoutTemplate();

        Assert.True(read.Unread);
        Assert.False(read.Reading);
        Assert.False(read.End(running, succeeded: true, WithAuditTable));
        Assert.Null(read.State);
    }
}
