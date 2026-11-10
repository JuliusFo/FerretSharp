namespace FerretSharp.Core.Query;

/// <summary>
/// The free SQL editor's view of a script (WP-17): tokens (comments, literals including <c>q'[…]'</c>, quoted names, binds),
/// statements separated by <c>;</c> or a line with only <c>/</c>, the statement at the cursor, and what kind
/// of statement it is. Deliberately not a parser – the database decides what is valid, the session guards decide what runs.
/// </summary>
/// <remarks>
/// Since R4b a facade over four parts: <see cref="SqlTokenizer"/>, <see cref="SqlSplitter"/>, <see cref="SqlClassifier"/>
/// and <see cref="SqlAnalysis"/>. The session's statement guards use only the tokenizer and the classifier.
/// </remarks>
public static class SqlScript
{
    public static IReadOnlyList<SqlToken> Tokenize(string text) => SqlTokenizer.Tokenize(text);

    /// <inheritdoc cref="SqlSplitter.Split"/>
    public static IReadOnlyList<SqlStatement> Split(string text) => SqlSplitter.Split(text);

    /// <inheritdoc cref="SqlSplitter.StatementAt"/>
    public static SqlStatement? StatementAt(string text, int selectionStart, int selectionEnd) => SqlSplitter.StatementAt(text, selectionStart, selectionEnd);

    /// <inheritdoc cref="SqlSplitter.Terminated"/>
    public static string Terminated(string text) => SqlSplitter.Terminated(text);

    /// <inheritdoc cref="SqlSplitter.Append"/>
    public static (string Text, int Start) Append(string script, string statement) => SqlSplitter.Append(script, statement);

    /// <inheritdoc cref="SqlSplitter.Insert"/>
    public static (string Text, int Start) Insert(string script, int start, int end, string statement) => SqlSplitter.Insert(script, start, end, statement);

    /// <inheritdoc cref="SqlSplitter.CountStatements"/>
    public static int CountStatements(string text) => SqlSplitter.CountStatements(text);

    /// <inheritdoc cref="SqlSplitter.Find"/>
    public static SqlStatement? Find(string script, SqlStatement statement) => SqlSplitter.Find(script, statement);

    /// <inheritdoc cref="SqlSplitter.InsertionPoint"/>
    public static int InsertionPoint(string script, int cursor) => SqlSplitter.InsertionPoint(script, cursor);

    /// <inheritdoc cref="SqlSplitter.StatementsIn"/>
    public static IReadOnlyList<SqlStatement> StatementsIn(string text, int selectionStart, int selectionEnd) => SqlSplitter.StatementsIn(text, selectionStart, selectionEnd);

    /// <inheritdoc cref="SqlAnalysis.DdlRejection"/>
    public static string? DdlRejection(string statement) => SqlAnalysis.DdlRejection(statement);

    /// <inheritdoc cref="SqlAnalysis.Analyze"/>
    public static SqlStatementInfo Analyze(string statement) => SqlAnalysis.Analyze(statement);

    /// <inheritdoc cref="SqlAnalysis.DdlTableOf"/>
    public static SqlTableReference? DdlTableOf(string statement) => SqlAnalysis.DdlTableOf(statement);

    /// <summary>
    /// A statement on one line, for lists and labels: line breaks and runs of blanks become one blank (EF writes over
    /// several lines). R5b: was written three times.
    /// </summary>
    public static string OneLine(string sql) => string.Join(' ', sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// The first word of a statement in upper case, comments skipped (<c>SELECT</c>, <c>UPDATE</c>) – for labels; R5b: the
    /// LINQ console showed "--" for a query with <c>TagWith</c>.
    /// </summary>
    public static string FirstWord(string sql) => SqlTokenizer.Significant(sql) is { Count: > 0 } tokens
        ? SqlClassifier.FirstWordOf(tokens)
        : "";
}
