using FerretSharp.Core.Query;
using FerretSharp.Core.Settings;

namespace FerretSharp.UI.State;

/// <summary>
/// The incoming FK jumps of the row a form shows, and their counts (WP-21, WP-56; review 3.44.0, B1: out of both form
/// panels). They are found once the row stays <see cref="Delay"/> – arrowing through rows would count each one – and
/// counted at once or on hover (<see cref="FkCountMode"/>); showing another row or jumping cancels them.
/// </summary>
/// <param name="mode">The setting as it is now.</param>
/// <param name="onUi">Runs on the UI thread (the component's <c>InvokeAsync</c>).</param>
/// <param name="changed">The jumps were found (on the UI thread).</param>
/// <param name="time">The clock of <see cref="Delay"/> (tests); the system's if null.</param>
public sealed class IncomingCounts(
    FkCounts counts, Func<FkCountMode> mode, Func<Func<Task>, Task> onUi, Action changed, TimeProvider? time = null) : IDisposable
{
    /// <summary>How long a row stays shown before its incoming jumps are found and counted.</summary>
    public static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(400);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    public FkCounts Counts => counts;

    /// <summary>The jumps of the shown row; empty until it stayed <see cref="Delay"/>.</summary>
    public IReadOnlyList<FkJump> Jumps { get; private set; } = [];

    /// <summary>Another row is shown: what ran for the previous one stops, its jumps are found after the delay.</summary>
    /// <param name="find">The jumps of the row shown then (called on the UI thread); null if it is not loaded (yet).</param>
    /// <returns>The wait and the counts; ends quietly when cancelled.</returns>
    public async Task StartAsync(Func<IReadOnlyList<FkJump>?> find)
    {
        Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        Jumps = [];
        var token = _cts.Token;
        try
        {
            await Task.Delay(Delay, _time, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await onUi(async () =>
        {
            if (_disposed || token.IsCancellationRequested || find() is not { } jumps)
            {
                return;
            }

            Jumps = jumps;
            counts.Reset();
            changed();
            if (mode() == FkCountMode.Immediately)
            {
                await counts.CountAllAsync(jumps, token);
            }
        });
    }

    /// <summary>Counts the entry the mouse rests on (<see cref="FkCountMode.OnHover"/>).</summary>
    public Task HoverAsync(FkJump jump) =>
        mode() == FkCountMode.OnHover && _cts is { IsCancellationRequested: false } cts ? counts.HoverAsync(jump, cts.Token) : Task.CompletedTask;

    /// <summary>
    /// Stops the counts: they run on the workspace's session, and a jump's new tab would wait for all of them for its first page.
    /// </summary>
    public void Cancel() => _cts?.Cancel();

    public void Dispose()
    {
        _disposed = true;
        Cancel();
        _cts?.Dispose();
    }
}
