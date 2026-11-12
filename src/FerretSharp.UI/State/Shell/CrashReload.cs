namespace FerretSharp.UI.State;

/// <summary>What became of the pending grid changes before a reload after a crash.</summary>
public enum CrashReloadChoice
{
    /// <summary>Nothing was pending: load without asking.</summary>
    NothingPending,

    /// <summary>The user keeps the page as it is (closing asks about the changes).</summary>
    Kept,

    /// <summary>The user discards the changes and loads anew.</summary>
    Discarded,

    /// <summary>The changes were written (no commit), then the page loads anew.</summary>
    Written,

    /// <summary>Not all could be written; the user keeps the page.</summary>
    NotAllWrittenKept,

    /// <summary>Not all could be written; the user loads anew all the same, the rest is lost.</summary>
    NotAllWrittenReloaded,
}

/// <param name="Pending">How many grid changes were not written when the user was asked.</param>
public sealed record CrashReloadDecision(CrashReloadChoice Choice, int Pending)
{
    public bool Reload => Choice is CrashReloadChoice.NothingPending or CrashReloadChoice.Discarded or CrashReloadChoice.Written
        or CrashReloadChoice.NotAllWrittenReloaded;
}

/// <summary>
/// Whether the host loads the page anew after the WebView's renderer crashed (review 3.70.0, A2; code review of R9, finding 1):
/// grid changes not written yet live in the page and would go with it, so with any the user is asked first – write them (no
/// commit) and load, discard them and load, or leave the page dead (closing then asks about them). The choice comes back with
/// the count for the log: "my changes are gone" must be traceable (third pass of the code review).
/// </summary>
public static class CrashReload
{
    /// <param name="askPending">Asks about that many pending changes: true write, false discard, null keep the page.</param>
    /// <param name="askAnyway">Not everything could be written: load anyway?</param>
    public static async Task<CrashReloadDecision> DecideAsync(ExitGuard exitGuard, Func<int, bool?> askPending, Func<bool> askAnyway)
    {
        ArgumentNullException.ThrowIfNull(exitGuard);
        ArgumentNullException.ThrowIfNull(askPending);
        ArgumentNullException.ThrowIfNull(askAnyway);
        var pending = exitGuard.PendingChanges is { } count ? await count() : 0;
        if (pending == 0)
        {
            return new CrashReloadDecision(CrashReloadChoice.NothingPending, 0);
        }

        var choice = askPending(pending) switch
        {
            null => CrashReloadChoice.Kept,
            false => CrashReloadChoice.Discarded,
            true when exitGuard.FlushAll is not { } flush || await flush() => CrashReloadChoice.Written,
            true => askAnyway() ? CrashReloadChoice.NotAllWrittenReloaded : CrashReloadChoice.NotAllWrittenKept,
        };
        return new CrashReloadDecision(choice, pending);
    }
}
