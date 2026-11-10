namespace FerretSharp.Core.Schema;

/// <summary>The clause after a view's query (WP-82): kept by Oracle as a constraint of the view, not in its text.</summary>
public enum ViewOption
{
    None,
    ReadOnly,
    CheckOption,
}

/// <summary>How a materialized view is refreshed and built (<c>ALL_MVIEWS</c>, WP-82).</summary>
/// <param name="RefreshMethod"><c>COMPLETE</c>, <c>FORCE</c>, <c>FAST</c> or <c>NEVER</c>.</param>
/// <param name="RefreshMode"><c>DEMAND</c>, <c>COMMIT</c>, <c>STATEMENT</c> or <c>NEVER</c>.</param>
/// <param name="QueryRewrite"><c>ENABLE QUERY REWRITE</c>.</param>
/// <param name="Prebuilt">Built on a table that existed before (<c>ON PREBUILT TABLE</c>).</param>
public sealed record MViewOptions(string RefreshMethod, string RefreshMode, bool QueryRewrite, bool Prebuilt)
{
    /// <summary><c>REFRESH FORCE ON DEMAND</c>, <c>NEVER REFRESH</c>: as in CREATE MATERIALIZED VIEW.</summary>
    public string RefreshClause =>
        RefreshMethod == "NEVER" || RefreshMode == "NEVER" ? "NEVER REFRESH" : $"REFRESH {RefreshMethod} ON {RefreshMode}";
}
