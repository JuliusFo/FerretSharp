namespace FerretSharp.Core.Connections;

/// <summary>
/// When reconnecting by itself (<see cref="Settings.AppSettings.AutoReconnect"/>) tries again: while the network or the
/// database is away – the VPN after sleep, a restarting instance –, with growing pauses for up to <see cref="GiveUpAfter"/>.
/// Never after a refused login: another wrong password counts towards <c>FAILED_LOGIN_ATTEMPTS</c> and can lock the account.
/// </summary>
public static class ReconnectPolicy
{
    /// <summary>The pauses before the second, third … attempt; after the last one <see cref="LaterPause"/>.</summary>
    public static readonly IReadOnlyList<TimeSpan> Pauses = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30)];

    public static readonly TimeSpan LaterPause = TimeSpan.FromMinutes(1);

    /// <summary>No attempt starts later than this after the connection was lost.</summary>
    public static readonly TimeSpan GiveUpAfter = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Opening a session may hang beyond ODP.NET's own 15 s (2026-10-21, after sleep); an attempt gives up on it after this
    /// and counts as failed (<see cref="ConnectionError.TimedOut"/>). Loading the schema afterwards has no limit.
    /// </summary>
    public static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Errors that say the database is not reachable or not open yet; everything else ends the attempts.</summary>
    private static readonly HashSet<string> Unavailable =
    [
        "ORA-01033", // initialization or shutdown in progress
        "ORA-01034", // not available
        "ORA-01089", // immediate shutdown in progress
        "ORA-01090", // shutdown in progress
        "ORA-50000", // ODP.NET managed: connection request timed out
    ];

    /// <summary>
    /// The pause before attempt <paramref name="attempt"/> + 1, after <paramref name="sinceLost"/> since the loss; null to give up.
    /// </summary>
    /// <param name="attempt">Attempts made so far (at least one).</param>
    public static TimeSpan? PauseAfter(int attempt, TimeSpan sinceLost)
    {
        var pause = attempt <= Pauses.Count ? Pauses[Math.Max(attempt, 1) - 1] : LaterPause;
        return sinceLost + pause <= GiveUpAfter ? pause : null;
    }

    /// <summary>The attempt failed in a way another one may fix: the network, the listener or the instance is away.</summary>
    public static bool IsWorthRetrying(ConnectionError? error) =>
        error is not null && (error.TimedOut || IsNetwork(error.ErrorCode));

    private static bool IsNetwork(string? code) =>
        code is not null && (DatabaseException.IsConnectionLostCode(code) || Unavailable.Contains(code) || IsTns(code));

    /// <summary>ORA-12150 … ORA-12699: TNS and network (no listener, host unreachable, connect timeout, service not registered yet).</summary>
    private static bool IsTns(string code) =>
        code.StartsWith("ORA-", StringComparison.Ordinal) && int.TryParse(code.AsSpan(4), out var number) && number is >= 12150 and <= 12699;
}
