using System.Globalization;
using System.Text;
using FerretSharp.Core.Resources;

namespace FerretSharp.Core.Query;

/// <summary>A script with a list bind spread over one bind per value (<see cref="SqlBindLists.Expand"/>).</summary>
/// <param name="Names">The new binds in the order of the values.</param>
public sealed record SqlBindList(string Text, IReadOnlyList<string> Names);

/// <summary>
/// A bind that takes several values (WP-84, <c>-- @param ids number list</c>): Oracle binds no lists, so
/// <c>IN (:ids)</c> becomes <c>IN (:ids_1, :ids_2, …)</c> – every value stays a bind, nothing turns into a literal. Only a
/// bind that stands alone in the parentheses of an <c>IN</c> is spread; anywhere else a list would change what the
/// statement means. Built on the editor's tokenizer, so comments and literals that look like the bind stay as they are.
/// </summary>
public static class SqlBindLists
{
    /// <summary>Oracle takes at most 1000 expressions in a list (ORA-01795).</summary>
    public const int MaxValues = 1000;

    /// <summary>Oracle's limit for the name of a bind, in bytes (ORA-00972 beyond it).</summary>
    private const int MaxNameBytes = 30;

    /// <summary>
    /// The spread text at most (characters): a script from a shared folder with many uses of the bind would otherwise grow
    /// to gigabytes (review WP-84, M1). Four times the largest script file.
    /// </summary>
    public const int MaxTextLength = 4 * 1024 * 1024;

    /// <summary>
    /// <paramref name="script"/> with each use of <c>:<paramref name="bind"/></c> replaced by <paramref name="count"/> binds;
    /// null with <paramref name="refusal"/> if a use is not alone in an <c>IN ( … )</c>, the script does not use it, or the
    /// count is beyond <see cref="MaxValues"/>, or the text would grow beyond <see cref="MaxTextLength"/>. The new names
    /// are not used elsewhere in the script nor among <paramref name="reserved"/> (the script's other parameters).
    /// </summary>
    public static SqlBindList? Expand(string script, string bind, int count, out string? refusal, IEnumerable<string>? reserved = null)
    {
        ArgumentNullException.ThrowIfNull(script);
        ArgumentNullException.ThrowIfNull(bind);
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        if (count > MaxValues)
        {
            refusal = TextFormat.Format(QueryText.BindListTooMany, MaxValues, count);
            return null;
        }

        var tokens = SqlTokenizer.Significant(script);
        var uses = new List<SqlToken>();
        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Kind != SqlTokenKind.Bind || !string.Equals(tokens[i].Value, bind, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (i < 2 || !tokens[i - 1].IsSymbol("(") || !tokens[i - 2].IsWord("IN") || i + 1 >= tokens.Count || !tokens[i + 1].IsSymbol(")"))
            {
                refusal = TextFormat.Format(QueryText.BindListPlacement, bind);
                return null;
            }

            uses.Add(tokens[i]);
        }

        if (uses.Count == 0)
        {
            refusal = TextFormat.Format(QueryText.BindListMissing, bind);
            return null;
        }

        var taken = new HashSet<string>(tokens.Where(t => t.Kind == SqlTokenKind.Bind).Select(t => t.Value), StringComparer.OrdinalIgnoreCase);
        taken.UnionWith(reserved ?? []);
        var names = Names(bind, count, taken);
        var list = string.Join(", ", names.Select(n => ":" + n));
        if (script.Length + (long)uses.Count * list.Length > MaxTextLength)
        {
            refusal = TextFormat.Format(QueryText.BindListTooLong, bind);
            return null;
        }

        // Once over the text: replacing use by use copied the whole text each time (review WP-84, M1).
        var text = new StringBuilder(script.Length + uses.Count * list.Length);
        var at = 0;
        foreach (var use in uses)
        {
            text.Append(script, at, use.Start - at).Append(list);
            at = use.End;
        }

        refusal = null;
        return new SqlBindList(text.Append(script, at, script.Length - at).ToString(), names);
    }

    /// <summary>
    /// <c>bind_1</c> … <c>bind_n</c>; a name with characters a bind without quotes cannot have, or too long for Oracle with
    /// the number, gets a short stem. Taken: <c>v1_1</c> …, then <c>v2_1</c> … – each attempt a new stem, so it ends once
    /// past the names taken (an underscore more and a reset made a cycle, review WP-84, S2).
    /// </summary>
    private static List<string> Names(string bind, int count, HashSet<string> taken)
    {
        var suffix = count.ToString(CultureInfo.InvariantCulture).Length + 1;
        var stem = bind.Length > 0 && char.IsAsciiLetter(bind[0]) && bind.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') ? bind : "v";
        if (stem.Length + suffix > MaxNameBytes)
        {
            stem = stem[..(MaxNameBytes - suffix)];
        }

        for (var attempt = 1; ; attempt++)
        {
            var names = Enumerable.Range(1, count).Select(i => stem + "_" + i.ToString(CultureInfo.InvariantCulture)).ToList();
            if (!names.Any(taken.Contains))
            {
                return names;
            }

            stem = "v" + attempt.ToString(CultureInfo.InvariantCulture);
        }
    }
}
