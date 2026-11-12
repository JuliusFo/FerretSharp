using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>
/// Whether the page loads anew after the renderer crashed (code review of R9, finding 1): grid changes not written yet would
/// go with it, so with any the user is asked first.
/// </summary>
public sealed class CrashReloadTests
{
    private static ExitGuard Guard(int pending, bool flushed, List<string> calls) => new()
    {
        PendingChanges = () => Task.FromResult(pending),
        FlushAll = () =>
        {
            calls.Add("flush");
            return Task.FromResult(flushed);
        },
    };

    [Theory]
    [InlineData(0, null, true, false, true, "")] // nothing pending: load without asking
    [InlineData(3, null, true, false, false, "ask")] // keep the page as it is
    [InlineData(3, false, true, false, true, "ask")] // discard and load: nothing written
    [InlineData(3, true, true, false, true, "ask flush")] // write, then load
    [InlineData(3, true, false, false, false, "ask flush anyway")] // not all written, keep the page
    [InlineData(3, true, false, true, true, "ask flush anyway")] // not all written, load all the same
    public async Task The_page_loads_anew_only_as_the_user_chose_for_pending_changes(
        int pending, bool? answer, bool flushed, bool anyway, bool reload, string expected)
    {
        var calls = new List<string>();
        var guard = Guard(pending, flushed, calls);

        var result = await CrashReload.ShouldReloadAsync(
            guard,
            count =>
            {
                calls.Insert(0, "ask");
                Assert.Equal(pending, count);
                return answer;
            },
            () =>
            {
                calls.Add("anyway");
                return anyway;
            });

        Assert.Equal(reload, result);
        Assert.Equal(expected, string.Join(' ', calls));
    }
}
