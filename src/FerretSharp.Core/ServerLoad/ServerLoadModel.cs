using FerretSharp.Core.Connections;

namespace FerretSharp.Core.ServerLoad;

/// <summary>
/// Reads the load of the database server and the figures of its statements (WP-75, ADR 0045). Only views outside the
/// Diagnostics and Tuning Pack: <c>V$OSSTAT</c>, <c>V$SYS_TIME_MODEL</c>, <c>V$SYSSTAT</c>, <c>V$SYSTEM_WAIT_CLASS</c>,
/// <c>V$INSTANCE</c>, <c>V$SQLSTATS</c>, <c>V$SQLAREA</c>, <c>V$SQL</c>, <c>V$SESSION</c> – never ASH, AWR, the metric views
/// or the SQL monitor (<c>PackViewTests</c>).
/// </summary>
/// <remarks>Every method throws <see cref="ServerLoadUnavailableException"/> when the user may not read the V$ views.</remarks>
public interface IServerLoadReader
{
    /// <summary>Container, instances and CPUs; touches every view the page reads, so it doubles as the rights check.</summary>
    Task<ServerEnvironment> ReadEnvironmentAsync(CancellationToken cancellationToken);

    /// <summary>The server's counters now: time, CPU, DB time, executions, foreground wait time per class.</summary>
    Task<LoadCounters> ReadCountersAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The figures of every cursor in the shared pool (<paramref name="activeSince"/> null: the baseline, thousands of rows) or of
    /// those active since a server time (<c>LAST_ACTIVE_TIME</c>, whole seconds). Numbers only, no text.
    /// </summary>
    Task<IReadOnlyList<StatementCounters>> ReadStatementsAsync(DateTime? activeSince, CancellationToken cancellationToken);

    /// <summary>Text (first 1000 characters), module and parsing schema of statements; one missing from the shared pool is left out.</summary>
    Task<IReadOnlyList<StatementInfo>> ReadStatementInfoAsync(IReadOnlyList<string> sqlIds, CancellationToken cancellationToken);

    /// <summary>
    /// The top statements by the figures since each was loaded into the shared pool (<c>V$SQLAREA</c>, like the reports of
    /// SQL Developer): sorted and cut on the server.
    /// </summary>
    Task<IReadOnlyList<StatementRow>> ReadTopSinceLoadAsync(StatementSort sort, StatementFilter filter, int count, CancellationToken cancellationToken);

    /// <summary>The user sessions running a call now, without the reading session itself.</summary>
    Task<IReadOnlyList<ActiveSession>> ReadActiveSessionsAsync(CancellationToken cancellationToken);
}

/// <summary>The user may not read the V$ views the server load needs.</summary>
public sealed class ServerLoadUnavailableException(string message, Exception? inner = null) : DatabaseException(message, null, inner);

/// <summary>How the server is set up, read once per session.</summary>
/// <param name="ContainerId"><c>CON_ID</c>: 0 no container database, 1 its root, 3 and up a pluggable database.</param>
/// <param name="Instances">Instances of the database (RAC: more than one; the page shows its own only).</param>
public sealed record ServerEnvironment(int ContainerId, int Instances, int Cpus)
{
    /// <summary>A pluggable database: its statements are its own, the host CPU is the whole host's.</summary>
    public bool IsPluggable => ContainerId >= 3;

    public bool IsCluster => Instances > 1;
}

/// <summary>The server's counters at one moment; all of them only grow until the instance restarts.</summary>
/// <param name="At">Server time (<c>SYSTIMESTAMP</c> in the server's own time zone, the one of <c>LAST_ACTIVE_TIME</c>).</param>
/// <param name="StartedAt"><c>V$INSTANCE.STARTUP_TIME</c>: changes when the instance restarted and the counters began again.</param>
/// <param name="BusyTime">CPU time of all CPUs of the host in centiseconds (<c>V$OSSTAT</c>); null where the platform does not count it.</param>
/// <param name="DbTime">Time of all sessions in calls in microseconds (<c>V$SYS_TIME_MODEL</c>).</param>
/// <param name="WaitTime">Foreground wait time per wait class in microseconds, without Idle.</param>
public sealed record LoadCounters(
    DateTime At,
    DateTime StartedAt,
    int Cpus,
    long? BusyTime,
    long? IdleTime,
    long DbTime,
    long DbCpu,
    long Executions,
    long LogicalReads,
    long Commits,
    IReadOnlyDictionary<string, long> WaitTime);

/// <summary>The load between two looks, as averages over that time.</summary>
/// <param name="HostCpu">Share of the host's CPU time in use, 0 to 1; null without <c>BUSY_TIME</c>/<c>IDLE_TIME</c>.</param>
/// <param name="ActiveSessions">Average active sessions: DB time per wall time. Compared with <see cref="Cpus"/> it says how loaded the server is.</param>
/// <param name="CpuSessions">The part of <see cref="ActiveSessions"/> on CPU (DB CPU per wall time).</param>
/// <param name="WaitingSessions">The part waiting, per wait class (foreground wait time per wall time).</param>
/// <param name="AfterGap">Not looked for a while (page closed): the averages span the whole gap.</param>
public sealed record LoadSample(
    DateTime From,
    DateTime To,
    int Cpus,
    double? HostCpu,
    double ActiveSessions,
    double CpuSessions,
    double ExecutionsPerSecond,
    double LogicalReadsPerSecond,
    double CommitsPerSecond,
    IReadOnlyDictionary<string, double> WaitingSessions,
    bool AfterGap)
{
    /// <summary>The share of DB time on CPU, 0 to 1; null if no session was active.</summary>
    public double? DbCpuShare => ActiveSessions > 0 ? Math.Min(1, CpuSessions / ActiveSessions) : null;

    /// <summary>The wait class with the most waiting sessions, if any waited.</summary>
    public KeyValuePair<string, double>? TopWaitClass =>
        WaitingSessions.Where(w => w.Value > 0).OrderByDescending(w => w.Value).Cast<KeyValuePair<string, double>?>().FirstOrDefault();
}

/// <summary>The figures of a statement (one cursor, or the sum over several); times in microseconds.</summary>
public readonly record struct StatementFigures(
    long Executions,
    long CpuTime,
    long ElapsedTime,
    long BufferGets,
    long DiskReads,
    long Rows,
    long ParseCalls,
    long UserIoWait,
    long ConcurrencyWait,
    long ApplicationWait)
{
    public bool IsZero => this == default;

    /// <summary>Some figure is smaller than in <paramref name="earlier"/>: the cursor was loaded again and counts from 0.</summary>
    public bool AnyBelow(StatementFigures earlier) =>
        Executions < earlier.Executions || CpuTime < earlier.CpuTime || ElapsedTime < earlier.ElapsedTime || BufferGets < earlier.BufferGets
        || DiskReads < earlier.DiskReads || Rows < earlier.Rows || ParseCalls < earlier.ParseCalls || UserIoWait < earlier.UserIoWait
        || ConcurrencyWait < earlier.ConcurrencyWait || ApplicationWait < earlier.ApplicationWait;

    public static StatementFigures operator +(StatementFigures a, StatementFigures b) =>
        new(a.Executions + b.Executions, a.CpuTime + b.CpuTime, a.ElapsedTime + b.ElapsedTime, a.BufferGets + b.BufferGets,
            a.DiskReads + b.DiskReads, a.Rows + b.Rows, a.ParseCalls + b.ParseCalls, a.UserIoWait + b.UserIoWait,
            a.ConcurrencyWait + b.ConcurrencyWait, a.ApplicationWait + b.ApplicationWait);

    public static StatementFigures operator -(StatementFigures a, StatementFigures b) =>
        new(a.Executions - b.Executions, a.CpuTime - b.CpuTime, a.ElapsedTime - b.ElapsedTime, a.BufferGets - b.BufferGets,
            a.DiskReads - b.DiskReads, a.Rows - b.Rows, a.ParseCalls - b.ParseCalls, a.UserIoWait - b.UserIoWait,
            a.ConcurrencyWait - b.ConcurrencyWait, a.ApplicationWait - b.ApplicationWait);

    /// <summary>The value a list is sorted by; per execution 0 for a statement not executed yet.</summary>
    public double ValueOf(StatementSort sort) => sort switch
    {
        StatementSort.Cpu => CpuTime,
        StatementSort.CpuPerExecution => PerExecution(CpuTime),
        StatementSort.Elapsed => ElapsedTime,
        StatementSort.ElapsedPerExecution => PerExecution(ElapsedTime),
        StatementSort.Executions => Executions,
        StatementSort.BufferGets => BufferGets,
        StatementSort.BufferGetsPerExecution => PerExecution(BufferGets),
        StatementSort.DiskReads => DiskReads,
        StatementSort.Rows => Rows,
        StatementSort.UserIoWait => UserIoWait,
        StatementSort.ConcurrencyWait => ConcurrencyWait,
        StatementSort.ApplicationWait => ApplicationWait,
        _ => throw new ArgumentOutOfRangeException(nameof(sort), sort, null),
    };

    public double PerExecution(long value) => Executions > 0 ? (double)value / Executions : 0;
}

/// <summary>What the top list is sorted by (descending).</summary>
public enum StatementSort
{
    Cpu,
    CpuPerExecution,
    Elapsed,
    ElapsedPerExecution,
    Executions,
    BufferGets,
    BufferGetsPerExecution,
    DiskReads,
    Rows,
    UserIoWait,
    ConcurrencyWait,
    ApplicationWait,
}

/// <summary>One cursor of <c>V$SQLSTATS</c>: a statement with one plan (in a container database per container).</summary>
public sealed record StatementCounters(string SqlId, long PlanHash, int ContainerId, StatementFigures Figures);

/// <summary>What is known of a statement besides its figures.</summary>
/// <param name="Text">The first 1000 characters (<c>V$SQL.SQL_TEXT</c>); the whole text comes with the cursor's details.</param>
/// <param name="Schema">The parsing schema: the user the statement ran as.</param>
public sealed record StatementInfo(string SqlId, string? Text, string? Module, string? Schema);

/// <summary>Filters the top list by parts of the parsing schema and of the module, ignoring case; empty matches all.</summary>
public sealed record StatementFilter(string? Schema = null, string? Module = null)
{
    public static readonly StatementFilter None = new();

    public bool IsEmpty => string.IsNullOrWhiteSpace(Schema) && string.IsNullOrWhiteSpace(Module);

    /// <summary>A statement whose info is not known does not match a filter that is set.</summary>
    public bool Matches(StatementInfo? info) =>
        IsEmpty || (info is not null && Contains(info.Schema, Schema) && Contains(info.Module, Module));

    private static bool Contains(string? value, string? part) =>
        string.IsNullOrWhiteSpace(part) || (value?.Contains(part.Trim(), StringComparison.OrdinalIgnoreCase) ?? false);
}

/// <summary>A line of the top list.</summary>
/// <param name="Plans">Plans seen in the window; null for the list since loading (<c>V$SQLAREA</c> sums them).</param>
/// <param name="Info">Null while it is being read, or when the cursor left the shared pool before.</param>
public sealed record StatementRow(string SqlId, int? Plans, StatementFigures Figures, StatementInfo? Info, DateTime? LastActive);

/// <summary>A user session running a call now (<c>V$SESSION</c>).</summary>
/// <param name="Event">What it waits for; null while it is on CPU.</param>
/// <param name="InCall">Since when the current call runs (<c>LAST_CALL_ET</c>).</param>
/// <param name="BlockingSid">The session holding it up (same instance), if any.</param>
public sealed record ActiveSession(
    int Sid,
    int Serial,
    string? User,
    string? OsUser,
    string? Machine,
    string? Program,
    string? Module,
    string? ClientInfo,
    string? SqlId,
    string? Event,
    string? WaitClass,
    TimeSpan InCall,
    int? BlockingSid)
{
    public bool OnCpu => Event is null;
}
