using System.Text;
using FerretSharp.Core.Connections;

namespace FerretSharp.Core.ServerLoad;

/// <summary>
/// Reads the load of the database server and the figures of its statements (WP-75, ADR 0045). Only views outside the
/// Diagnostics and Tuning Pack: <c>V$OSSTAT</c>, <c>V$SYS_TIME_MODEL</c>, <c>V$SYSSTAT</c>, <c>V$SYSTEM_WAIT_CLASS</c>,
/// <c>V$INSTANCE</c>, <c>V$SQLSTATS</c>, <c>V$SQL</c>, <c>V$SESSION</c> – never ASH, AWR, the metric views
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
    /// The figures of every statement in the shared pool (<paramref name="activeSince"/> null: the baseline, thousands of rows)
    /// or of those active since a server time (<c>LAST_ACTIVE_TIME</c>, whole seconds). Numbers only, no text.
    /// </summary>
    Task<IReadOnlyList<StatementCounters>> ReadStatementsAsync(DateTime? activeSince, CancellationToken cancellationToken);

    /// <summary>Text (first 1000 characters), module and parsing schema of statements; one missing from the shared pool is left out.</summary>
    Task<IReadOnlyList<StatementInfo>> ReadStatementInfoAsync(IReadOnlyList<string> sqlIds, CancellationToken cancellationToken);

    /// <summary>
    /// The whole text of a statement (<c>SQL_FULLTEXT</c> of <c>V$SQL</c>, else of <c>V$SQLSTATS</c> – also when the cursor left
    /// the library cache and figures and plan are gone); null when it is gone everywhere. What <c>V$SQLSTATS</c> keeps may be cut
    /// (<see cref="StatementText.MayBeCut"/>).
    /// </summary>
    Task<StatementText?> ReadFullTextAsync(string sqlId, CancellationToken cancellationToken);

    /// <summary>
    /// The top statements by the figures since each was loaded into the shared pool (<c>V$SQLSTATS</c>, like the reports of
    /// SQL Developer): sorted and cut on the server.
    /// </summary>
    Task<IReadOnlyList<StatementRow>> ReadTopSinceLoadAsync(TopStatementSort sort, StatementFilter filter, int count, CancellationToken cancellationToken);

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
    public double ValueOf(TopStatementSort sort) => sort switch
    {
        TopStatementSort.Cpu => CpuTime,
        TopStatementSort.CpuPerExecution => PerExecution(CpuTime),
        TopStatementSort.Elapsed => ElapsedTime,
        TopStatementSort.ElapsedPerExecution => PerExecution(ElapsedTime),
        TopStatementSort.Executions => Executions,
        TopStatementSort.BufferGets => BufferGets,
        TopStatementSort.BufferGetsPerExecution => PerExecution(BufferGets),
        TopStatementSort.DiskReads => DiskReads,
        TopStatementSort.Rows => Rows,
        TopStatementSort.UserIoWait => UserIoWait,
        TopStatementSort.ConcurrencyWait => ConcurrencyWait,
        TopStatementSort.ApplicationWait => ApplicationWait,
        _ => throw new ArgumentOutOfRangeException(nameof(sort), sort, null),
    };

    public double PerExecution(long value) => Executions > 0 ? (double)value / Executions : 0;
}

/// <summary>What the top list is sorted by (descending).</summary>
public enum TopStatementSort
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

/// <summary>
/// One row of <c>V$SQLSTATS</c>: a statement (in a container database per container) with its figures over all its plans;
/// <paramref name="PlanHash"/> is the plan of the child cursor active last, not the one the figures belong to.
/// </summary>
public sealed record StatementCounters(string SqlId, long PlanHash, int ContainerId, StatementFigures Figures);

/// <summary>What is known of a statement besides its figures.</summary>
/// <param name="Text">
/// The first <see cref="TextLength"/> characters (<c>V$SQLSTATS.SQL_TEXT</c>); the whole text comes with the details
/// (<see cref="IServerLoadReader.ReadFullTextAsync"/>).
/// </param>
/// <param name="Module">From <c>V$SQL</c>: none once the cursor left the library cache.</param>
/// <param name="Schema">The parsing schema (the user the statement ran as), from <c>V$SQL</c> like the module.</param>
public sealed record StatementInfo(string SqlId, string? Text, string? Module, string? Schema)
{
    /// <summary>The size of <c>SQL_TEXT</c> – in bytes of the database character set, not in characters.</summary>
    public const int TextLength = 1000;

    /// <summary>The text may be cut (review of WP-75): never take it for the whole statement, e.g. to run it.</summary>
    public bool MayBeCut => MayBeCutText(Text);

    /// <summary>
    /// Whether a text of <c>SQL_TEXT</c> (or what <c>V$SQLSTATS.SQL_FULLTEXT</c> keeps) may be cut. Oracle cuts after 1000
    /// <b>bytes</b>: with umlauts in AL32UTF8 that are 511 characters (second review of WP-75, measured). The database character
    /// set is not known here, so a text that reaches 1000 characters, or nearly 1000 bytes in UTF-8 (a character takes up to
    /// four), counts as maybe cut – a single-byte database may flag a whole text with many umlauts, never miss a cut one.
    /// </summary>
    public static bool MayBeCutText(string? text) =>
        text is not null && (text.Length >= TextLength || Encoding.UTF8.GetByteCount(text) >= TextLength - 3);
}

/// <summary>The whole text of a statement, or what is left of it.</summary>
/// <param name="MayBeCut">From <c>V$SQLSTATS</c>, which may keep only the first 1000 bytes: never take it for the whole statement.</param>
public sealed record StatementText(string Text, bool MayBeCut);

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
/// <param name="Plans">
/// The plans seen while the page watched, or in the list since loading the plans <c>V$SQLSTATS</c> names – with one row per
/// statement always 1 (backlog).
/// </param>
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
