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
/// <param name="PlanHashValue">
/// The largest <c>PLAN_HASH_VALUE</c> of the children: 0 only for a statement that has no plan at all (PL/SQL block,
/// <c>CALL</c>, <c>ALTER SESSION</c>, most DDL); a plan Oracle freed keeps its hash.
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
    ExecutionPlan Plan,
    long PlanHashValue)
{
    public TimeSpan AverageElapsed => Executions > 0 ? Elapsed / Executions : TimeSpan.Zero;

    public bool HasActualFigures => Plan.Steps.Any(s => s.ActualRows is not null);

    /// <summary>
    /// False when the plan has no steps: the statement has none (<see cref="HasNoPlanAtAll"/>), or Oracle has freed it but
    /// kept the cursor (heap 6 goes first under memory pressure; V$SQL_PLAN_STATISTICS_ALL has no rows until the next
    /// execution loads the plan again, fix-cursor-plan-child).
    /// </summary>
    public bool HasPlan => Plan.Steps.Count > 0;

    /// <summary>A statement that never has a plan (PL/SQL block, <c>CALL</c>, <c>ALTER SESSION</c>, most DDL – EF sends <c>SaveChanges</c> as a block).</summary>
    public bool HasNoPlanAtAll => !HasPlan && PlanHashValue == 0;
}
