namespace FerretSharp.UI.State;

/// <summary>
/// Whether the host loads the page anew after the WebView's renderer crashed (review 3.70.0, A2; code review of R9, finding 1):
/// grid changes not written yet live in the page and would go with it, so with any the user is asked first – write them (no
/// commit) and load, discard them and load, or leave the page dead (closing then asks about them).
/// </summary>
public static class CrashReload
{
    /// <param name="askPending">Asks about that many pending changes: true write, false discard, null keep the page.</param>
    /// <param name="askAnyway">Not everything could be written: load anyway?</param>
    public static async Task<bool> ShouldReloadAsync(ExitGuard exitGuard, Func<int, bool?> askPending, Func<bool> askAnyway)
    {
        ArgumentNullException.ThrowIfNull(exitGuard);
        ArgumentNullException.ThrowIfNull(askPending);
        ArgumentNullException.ThrowIfNull(askAnyway);
        var pending = exitGuard.PendingChanges is { } count ? await count() : 0;
        if (pending == 0)
        {
            return true;
        }

        return askPending(pending) switch
        {
            null => false,
            false => true,
            true => exitGuard.FlushAll is not { } flush || await flush() || askAnyway(),
        };
    }
}
