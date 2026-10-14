using System.Runtime.InteropServices;
using FerretSharp.UI.State;
using Microsoft.Extensions.Time.Testing;

namespace FerretSharp.UI.Tests.State;

/// <summary>
/// After sleep at a dock, WPF reported its failed render thread for every window message, and each error dialog caused
/// the next: the user is asked once per series whether to restart, the rest is only logged.
/// </summary>
public sealed class RenderFailureNoticeTests
{
    private readonly FakeTimeProvider _time = new();

    private static COMException Failure() => new("UCEERR_RENDERTHREADFAILURE (0x88980406)", RenderFailureNotice.RenderThreadFailure);

    [Fact]
    public void Recognizes_the_render_thread_failure_also_wrapped()
    {
        Assert.True(RenderFailureNotice.Matches(Failure()));
        Assert.True(RenderFailureNotice.Matches(new InvalidOperationException("outer", Failure())));
        Assert.False(RenderFailureNotice.Matches(new COMException("other", unchecked((int)0x80004005))));
        Assert.False(RenderFailureNotice.Matches(null));
    }

    [Fact]
    public void Asks_once_while_the_question_is_open_and_counts_the_repeats()
    {
        var notice = new RenderFailureNotice(_time);

        Assert.True(notice.Report());
        Assert.False(notice.Report());
        Assert.False(notice.Report());

        Assert.Equal(2, notice.Repeats);
    }

    [Fact]
    public void After_the_answer_a_series_is_asked_about_again_only_after_a_quiet_period()
    {
        var notice = new RenderFailureNotice(_time);
        Assert.True(notice.Report());
        notice.Answered();

        _time.Advance(TimeSpan.FromSeconds(10));
        Assert.False(notice.Report()); // the same series: the user just answered
        _time.Advance(RenderFailureNotice.Quiet);

        Assert.True(notice.Report());
        Assert.Equal(0, notice.Repeats);
    }
}
