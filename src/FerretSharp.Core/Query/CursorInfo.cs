namespace FerretSharp.Core.Query;

/// <summary>
/// What the shared pool knows about a statement another program ran (WP-33): the figures of <c>V$SQL</c> summed over all
/// children of its SQL_ID, and the plan of the child used last. The figures count every session of the instance, not
/// only the traced app.
/// </summary>
/// <param name="Children">Child cursors of the SQL_ID (several plans or environments).</param>
/// <param name="Plan">
/// The plan of the child used last, from <c>V$SQL_PLAN_STATISTICS_ALL</c>. Its actual figures (rows, starts, time) are
/// only there if the statement ran with statistics (<c>GATHER_PLAN_STATISTICS</c>, <c>STATISTICS_LEVEL = ALL</c>).
/// </param>
public sealed record CursorInfo(
    string SqlId,
    int Children,
    long Executions,
    long RowsProcessed,
    long BufferGets,
    long DiskReads,
    TimeSpan Elapsed,
    TimeSpan Cpu,
    DateTime? LastActive,
    ExecutionPlan Plan)
{
    public TimeSpan AverageElapsed => Executions > 0 ? Elapsed / Executions : TimeSpan.Zero;

    public bool HasActualFigures => Plan.Steps.Any(s => s.ActualRows is not null);
}
