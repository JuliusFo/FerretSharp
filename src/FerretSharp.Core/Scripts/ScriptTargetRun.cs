using FerretSharp.Core.Query;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Scripts;

/// <summary>
/// A script started from a cell (WP-84): its text, its variables with the cell's value – or the marked rows' values, for a
/// list parameter – for the target's parameter, the parameters still without a value, and whether it may run at once.
/// </summary>
/// <param name="Text">The script's SQL; with several values for a list parameter spread over one bind per value.</param>
/// <param name="Variables">One per bind of <see cref="Text"/>, typed by the script's <c>@param</c>; the target's with the value(s).</param>
/// <param name="Missing">Parameters other than the target's: the dialog asks for them before it runs.</param>
/// <param name="Spread">The text is spread (<see cref="SqlBindLists"/>): the tab is not linked to the file.</param>
/// <param name="Values">How many values the target's parameter got.</param>
/// <param name="ReadOnly">
/// Every statement of the script is a query: it runs at once (decision of the user, also on Prod – it only reads, like a jump
/// along a foreign key). Otherwise the tab only opens with the values; nothing that writes runs from a menu.
/// </param>
public sealed record ScriptTargetRun(
    string Text, IReadOnlyList<SqlVariable> Variables, IReadOnlyList<ScriptParameter> Missing, bool Spread, int Values, bool ReadOnly)
{
    /// <summary>
    /// The run of the script <paramref name="header"/> + <paramref name="body"/> by <paramref name="target"/> for cells of
    /// <paramref name="column"/>: <paramref name="values"/> holds the clicked cell's value first, then those of the other
    /// marked rows. A plain parameter takes the clicked cell's; a list parameter all of them (NULL left out, each value
    /// once). Null with <paramref name="refusal"/> if nothing can be handed on (NULL, a LOB, a float), the file no longer
    /// has the target, or the list does not fit.
    /// </summary>
    /// <param name="remembered">Values typed before for the other parameters (the dialog fills them in).</param>
    public static ScriptTargetRun? Of(
        ScriptHeader header, string body, ScriptTarget target, ColumnInfo column, IReadOnlyList<object?> values,
        IReadOnlyDictionary<string, string> remembered, out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(column);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(remembered);
        // The file may have changed since the menu was built (a pull): only a target it still has counts.
        if (!header.Targets.Contains(target)
            || header.Parameters.FirstOrDefault(p => string.Equals(p.Name, target.Parameter, StringComparison.OrdinalIgnoreCase)) is not { } parameter)
        {
            refusal = ScriptText.Target_Gone;
            return null;
        }

        var texts = Texts(parameter, column, values, out refusal);
        if (texts is null)
        {
            return null;
        }

        var text = body;
        IReadOnlyList<string> names = [parameter.Name];
        if (texts.Count > 1)
        {
            // The other parameters get a variable each: no spread name may be one of them (review WP-84, K3).
            if (SqlBindLists.Expand(body, parameter.Name, texts.Count, out refusal,
                    header.Parameters.Where(p => p != parameter).Select(p => p.Name)) is not { } list)
            {
                return null;
            }

            (text, names) = (list.Text, list.Names);
        }

        var national = parameter.Type is SqlVariableType.Text or SqlVariableType.Char && OracleTypes.IsNational(column.DataType);
        var variables = new List<SqlVariable>();
        var missing = new List<ScriptParameter>();
        foreach (var other in header.Parameters)
        {
            if (other == parameter)
            {
                variables.AddRange(names.Select((n, i) => new SqlVariable(n, parameter.Type, texts[i]) { National = national }));
            }
            else
            {
                variables.Add(new SqlVariable(other.Name, other.Type, remembered.FirstOrDefault(kv => string.Equals(kv.Key, other.Name, StringComparison.OrdinalIgnoreCase)).Value ?? ""));
                if (other.Type != SqlVariableType.Null)
                {
                    missing.Add(other);
                }
            }
        }

        refusal = null;
        return new ScriptTargetRun(text, variables, missing, texts.Count > 1, texts.Count, IsReadOnly(body));
    }

    /// <summary>Why the cell's value cannot be handed to a script; null if it can.</summary>
    public static string? Refusal(ColumnInfo column, object? value)
    {
        ArgumentNullException.ThrowIfNull(column);
        return value is null or DBNull ? ScriptText.Target_ValueIsNull
            : FkNavigation.FilterValue(column, value) is null ? ScriptText.Target_ValueNotPassable
            : null;
    }

    /// <summary>The script has statements and all of them are plain queries – no FOR UPDATE (<see cref="SqlStatementInfo.IsQuery"/>).</summary>
    public static bool IsReadOnly(string body)
    {
        var statements = SqlScript.Split(body).Where(s => SqlClassifier.KindOf(s.Text) != SqlStatementKind.Empty).ToList();
        return statements.Count > 0 && statements.All(s => SqlScript.Analyze(s.Text).IsQuery);
    }

    /// <summary>
    /// The value texts the parameter gets: the clicked cell's, or for a list parameter every value of the marked rows once
    /// (in their order, NULL left out). Null with <paramref name="refusal"/> if there is none to give.
    /// </summary>
    private static List<string>? Texts(ScriptParameter parameter, ColumnInfo column, IReadOnlyList<object?> values, out string? refusal)
    {
        var given = parameter.List ? values.Where(v => v is not (null or DBNull)).ToList() : values.Take(1).ToList();
        if (given.Count == 0)
        {
            refusal = ScriptText.Target_ValueIsNull;
            return null;
        }

        var texts = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in given)
        {
            if (Refusal(column, value) is { } problem)
            {
                refusal = problem;
                return null;
            }

            var text = FkNavigation.FilterValue(column, value!)!;
            if (seen.Add(text))
            {
                texts.Add(text);
            }
        }

        refusal = null;
        return texts;
    }
}
