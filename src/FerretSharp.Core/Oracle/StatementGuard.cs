using System.Globalization;
using FerretSharp.Core.Query;

namespace FerretSharp.Core.Oracle;

/// <summary>
/// The statement guards of <see cref="OracleSession"/> (CLAUDE.md section 2, ADR 0006): which statement may take which
/// path. Not a SQL parser – a tripwire against programming mistakes. Built on the SQL editor's tokenizer, so comments,
/// <c>q'[…]'</c> literals and quoted names are seen the same way the editor sees them (since WP-17 user SQL reaches the
/// read path, a comment with a <c>;</c> must not make a query look like two statements).
/// </summary>
internal static class StatementGuard
{
    /// <summary>A single plain query: starts with SELECT or WITH, no FOR UPDATE (row locks).</summary>
    public static bool IsQuery(string sql) =>
        Significant(sql) is { } tokens && StartsWithQuery(tokens) && !HasForUpdate(tokens);

    /// <summary>A single INSERT, UPDATE, DELETE or MERGE. Never DDL: it would commit implicitly, also inside a read-only transaction.</summary>
    public static bool IsWrite(string sql) =>
        Significant(sql) is [var first, ..] && (first.IsWord("INSERT") || first.IsWord("UPDATE") || first.IsWord("DELETE") || first.IsWord("MERGE"));

    /// <summary>A single query ending in FOR UPDATE WAIT n (up to 3 digits) or FOR UPDATE NOWAIT – never one that waits forever.</summary>
    public static bool IsLock(string sql) =>
        Significant(sql) is { } tokens && StartsWithQuery(tokens) && tokens switch
        {
            [.., var f, var u, var nowait] when f.IsWord("FOR") && u.IsWord("UPDATE") && nowait.IsWord("NOWAIT") => true,
            [.., var f, var u, var wait, var seconds] when f.IsWord("FOR") && u.IsWord("UPDATE") && wait.IsWord("WAIT") => IsWaitSeconds(seconds),
            _ => false,
        };

    /// <summary>The tokens without spaces and comments, without trailing <c>;</c>; null if that is no single statement.</summary>
    private static List<SqlToken>? Significant(string sql)
    {
        var tokens = SqlScript.Tokenize(sql).Where(t => t.Kind is not (SqlTokenKind.Space or SqlTokenKind.Comment)).ToList();
        while (tokens.Count > 0 && tokens[^1].IsSymbol(";"))
        {
            tokens.RemoveAt(tokens.Count - 1);
        }

        return tokens.Count == 0 || tokens.Any(t => t.IsSymbol(";")) ? null : tokens;
    }

    private static bool StartsWithQuery(List<SqlToken> tokens) => tokens[0].IsWord("SELECT") || tokens[0].IsWord("WITH");

    private static bool HasForUpdate(List<SqlToken> tokens)
    {
        for (var i = 0; i + 1 < tokens.Count; i++)
        {
            if (tokens[i].IsWord("FOR") && tokens[i + 1].IsWord("UPDATE"))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsWaitSeconds(SqlToken token) =>
        token.Kind == SqlTokenKind.Number && token.Value.Length <= 3 && int.TryParse(token.Value, NumberStyles.None, CultureInfo.InvariantCulture, out _);
}
