using System.Text.RegularExpressions;

namespace FerretSharp.Core.Query;

/// <summary>A statement of the SQL editor, checked and bound, ready to run.</summary>
/// <param name="Number">Position in the script (1, 2 …); 0 for a single statement (Ctrl+Enter).</param>
public sealed record PlannedStatement(int Number, SqlStatement Statement, SqlStatementInfo Info, QuerySpec Query);

/// <summary>Why nothing runs: a statement that would be refused, needs an unlocked workspace or a missing value.</summary>
/// <param name="Statement">The statement it is about (marked in the editor).</param>
/// <param name="NeedsUnlock">DML on a read-only workspace: unlocking it would help.</param>
public sealed record PlanProblem(SqlStatement Statement, string Message, bool NeedsUnlock);

/// <summary>
/// What the SQL editor is about to run (WP-17/18, R2 out of the view): every statement is checked before anything runs –
/// allowed at all, a writable workspace for DML, values for all binds – so a script never stops halfway over something
/// that was clear beforehand. Either <see cref="Statements"/> or <see cref="Problem"/>.
/// </summary>
public sealed record SqlScriptPlan(IReadOnlyList<PlannedStatement> Statements, PlanProblem? Problem)
{
    private static readonly Regex QuotedName = new("\"([^\"]+)\"", RegexOptions.CultureInvariant);

    /// <param name="single">Ctrl+Enter: one statement, numbered 0 and named without "Statement n".</param>
    public static SqlScriptPlan Prepare(IReadOnlyList<SqlStatement> statements, IReadOnlyList<SqlVariable> variables, bool writable, bool single)
    {
        var planned = new List<PlannedStatement>();
        for (var i = 0; i < statements.Count; i++)
        {
            var statement = statements[i];
            var info = SqlScript.Analyze(statement.Text);
            var (problem, needsUnlock) = (info.Rejection, false);
            QuerySpec? query = null;
            if (problem is null && !info.IsQuery && !writable)
            {
                (problem, needsUnlock) = ($"{info.FirstWord} ändert Daten – der Workspace ist schreibgeschützt.", true);
            }
            else if (problem is null)
            {
                try
                {
                    query = SqlBinds.Bind(statement.Text, info.Binds, variables);
                }
                catch (SqlBindException ex)
                {
                    problem = ex.Message;
                }
            }

            if (query is null)
            {
                return new SqlScriptPlan([], new PlanProblem(statement, single ? problem! : $"Statement {i + 1} – nichts ausgeführt: {problem}", needsUnlock));
            }

            planned.Add(new PlannedStatement(single ? 0 : i + 1, statement, info, query));
        }

        return new SqlScriptPlan(planned, null);
    }

    /// <summary>Asked before running: DML on Prod always, UPDATE/DELETE without WHERE everywhere (decision of the user, WP-17).</summary>
    public bool NeedsConfirmation(bool prod) => Statements.Any(s => s.Info.AffectsAllRows) || (prod && Statements.Any(s => s.Info.IsDml));

    /// <summary>
    /// Where in <paramref name="statement"/> the name an ORA-00904 (invalid identifier) or ORA-00942 (table or view does not
    /// exist) complains about is – to mark it in the editor; null for other errors or a name not found in the text.
    /// </summary>
    /// <returns>Offset and length within the statement.</returns>
    public static (int Start, int Length)? LocateError(string statement, string? errorCode, string message)
    {
        if (errorCode is not ("ORA-00904" or "ORA-00942") || QuotedName.Matches(message) is not { Count: > 0 } names)
        {
            return null;
        }

        // "FERRET"."GIBT_ES_NICHT" – the last quoted part is the name as written (or as Oracle stored it).
        var name = names[^1].Groups[1].Value;
        var at = Regex.Match(statement, $@"(?<![\w$#""]){Regex.Escape(name)}(?![\w$#])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return at.Success ? (at.Index, at.Length) : null;
    }
}
