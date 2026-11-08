namespace FerretSharp.Core.Query;

/// <summary>
/// The table an INSERT, UPDATE, DELETE or MERGE writes and the columns it sets (WP-76): the <c>SET</c> assignments and the
/// column list of an <c>INSERT</c> (also both branches of a <c>MERGE</c>); none for a <c>DELETE</c> or an <c>INSERT</c>
/// without a column list. Like <see cref="SqlAnalysis"/>, not a parser.
/// </summary>
/// <param name="Table">The first table the statement names: <c>UPDATE x</c>, <c>DELETE FROM x</c>, <c>INSERT INTO x</c>, <c>MERGE INTO x</c>.</param>
/// <param name="Columns">In dictionary form, each once.</param>
public sealed record SqlDmlTarget(SqlStatementKind Kind, SqlTableReference Table, IReadOnlyList<string> Columns)
{
    /// <summary>Words that end the assignments after a <c>SET</c>.</summary>
    private static readonly HashSet<string> SetEnd = new(StringComparer.OrdinalIgnoreCase)
    {
        "WHERE", "RETURNING", "RETURN", "WHEN", "LOG", "DELETE",
    };

    /// <summary>The target of a DML statement; null for anything else or one without a table.</summary>
    public static SqlDmlTarget? Of(string statement)
    {
        var info = SqlAnalysis.Analyze(statement);
        if (!info.IsDml || info.Tables.FirstOrDefault() is not { } table)
        {
            return null;
        }

        var tokens = SqlTokenizer.Significant(statement);
        var columns = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var depth = 0;
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.IsSymbol("("))
            {
                depth++;
            }
            else if (token.IsSymbol(")"))
            {
                depth = Math.Max(0, depth - 1);
            }
            else if (depth == 0 && token.IsWord("SET"))
            {
                i = Assignments(tokens, i + 1, Add) - 1;
            }
            else if (depth == 0 && token.IsWord("INSERT"))
            {
                i = InsertColumns(tokens, i + 1, Add) - 1;
            }
        }

        return new SqlDmlTarget(info.Kind, table, columns);

        void Add(string column)
        {
            if (seen.Add(column))
            {
                columns.Add(column);
            }
        }
    }

    /// <summary>
    /// <c>a = …, t.b = …, (c, d) = (SELECT …)</c> up to a word that ends them: the assigned columns; returns where it stopped.
    /// A <c>WHEN</c> inside a <c>CASE</c> of a value does not end them (second pass of review WP-76: EF Core writes a
    /// conditional <c>SetProperty</c> that way).
    /// </summary>
    private static int Assignments(List<SqlToken> tokens, int i, Action<string> add)
    {
        var expectColumn = true;
        var depth = 0;
        var cases = 0;
        for (; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (depth == 0 && cases == 0 && (token.IsSymbol(";") || (token.Kind == SqlTokenKind.Word && SetEnd.Contains(token.Value))))
            {
                return i;
            }

            if (token.IsWord("CASE"))
            {
                cases++;
            }
            else if (token.IsWord("END") && cases > 0)
            {
                cases--;
            }

            if (expectColumn && depth == 0 && token.IsSymbol("(") && ColumnList(tokens, i) is { } list)
            {
                foreach (var name in list.Names)
                {
                    add(name);
                }

                i = list.At;
                expectColumn = false;
            }
            else if (token.IsSymbol("("))
            {
                depth++;
            }
            else if (token.IsSymbol(")"))
            {
                depth = Math.Max(0, depth - 1);
            }
            else if (depth == 0 && token.IsSymbol(","))
            {
                expectColumn = true;
            }
            else if (expectColumn && depth == 0 && LastNameBefore(tokens, i, "=") is { } column)
            {
                add(column.Name);
                i = column.At;
                expectColumn = false;
            }
            else
            {
                expectColumn = false;
            }
        }

        return i;
    }

    /// <summary><c>(a, t.b) =</c> at <paramref name="i"/>: the names (last parts) and where the <c>=</c> is.</summary>
    private static (List<string> Names, int At)? ColumnList(List<SqlToken> tokens, int i)
    {
        var names = new List<string>();
        for (i++; i < tokens.Count && !tokens[i].IsSymbol(")"); i++)
        {
            if (tokens[i].IsSymbol(",") || tokens[i].IsSymbol("."))
            {
                continue;
            }

            if (tokens[i].Kind is not (SqlTokenKind.Word or SqlTokenKind.QuotedIdentifier))
            {
                return null;
            }

            if (i + 1 >= tokens.Count || !tokens[i + 1].IsSymbol("."))
            {
                names.Add(SqlAnalysis.Normalize(tokens[i]));
            }
        }

        return names.Count > 0 && i + 1 < tokens.Count && tokens[i + 1].IsSymbol("=") ? (names, i + 1) : null;
    }

    /// <summary>A name, maybe qualified, at <paramref name="i"/> followed by <paramref name="symbol"/>: its last part and where the symbol is.</summary>
    private static (string Name, int At)? LastNameBefore(List<SqlToken> tokens, int i, string symbol)
    {
        string? name = null;
        while (i < tokens.Count && tokens[i].Kind is SqlTokenKind.Word or SqlTokenKind.QuotedIdentifier)
        {
            name = SqlAnalysis.Normalize(tokens[i]);
            if (i + 2 < tokens.Count && tokens[i + 1].IsSymbol("."))
            {
                i += 2;
                continue;
            }

            i++;
            break;
        }

        return name is not null && i < tokens.Count && tokens[i].IsSymbol(symbol) ? (name, i) : null;
    }

    /// <summary>
    /// The column list after <c>INSERT [INTO t [alias]]</c>: the names in the parenthesis before <c>VALUES</c> or the query;
    /// returns where it stopped.
    /// </summary>
    private static int InsertColumns(List<SqlToken> tokens, int i, Action<string> add)
    {
        for (; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.IsSymbol(";") || token.IsWord("VALUES") || token.IsWord("SELECT") || token.IsWord("WITH"))
            {
                return i;
            }

            if (!token.IsSymbol("("))
            {
                continue;
            }

            for (i++; i < tokens.Count && !tokens[i].IsSymbol(")"); i++)
            {
                if (tokens[i].Kind is SqlTokenKind.Word or SqlTokenKind.QuotedIdentifier
                    && (i + 1 >= tokens.Count || !tokens[i + 1].IsSymbol(".")))
                {
                    add(SqlAnalysis.Normalize(tokens[i]));
                }
            }

            return i;
        }

        return i;
    }
}
