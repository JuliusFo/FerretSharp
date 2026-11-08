using FerretSharp.Core.Query;

namespace FerretSharp.Core.ClrModel;

/// <summary>Why a method may have sent a statement (WP-76).</summary>
public enum CodeOriginReasonKind
{
    /// <summary>The method queries the table: <c>Set&lt;T&gt;()</c> or the context's DbSet property.</summary>
    Queries,

    /// <summary>The method reaches the table through a navigation in a query (<c>Include</c>, <c>o.Customer.Name</c> in LINQ).</summary>
    Joins,

    /// <summary>The method reads a navigation to the table outside a query: with lazy loading, that loads its rows.</summary>
    LazyLoads,

    /// <summary>The method filters by columns the statement filters by too.</summary>
    Filters,

    /// <summary>
    /// The method writes properties whose columns a DML statement sets in its target table (<c>SaveChanges</c>,
    /// <c>ExecuteUpdate</c>): the <c>SET</c> or <c>INSERT</c> columns, none for a <c>DELETE</c>.
    /// </summary>
    Writes,
}

/// <param name="Table">The statement's table, as it names it.</param>
/// <param name="Columns">For <see cref="CodeOriginReasonKind.Filters"/> and <see cref="CodeOriginReasonKind.Writes"/>: the columns.</param>
/// <param name="Navigation">For joins and lazy loads: the navigation, as <c>Entity.Navigation</c>.</param>
public sealed record CodeOriginReason(CodeOriginReasonKind Kind, string Table, IReadOnlyList<string> Columns, string? Navigation = null);

/// <summary>A method that may have sent the statement, with the place to jump to and why it fits.</summary>
/// <param name="Use">The first matching use in the method (by line): its file and line are the place shown.</param>
/// <param name="Score">Higher fits better; only the order counts, not the number.</param>
/// <param name="Fit">How much of the statement the method covers: what is shown of how well it fits.</param>
public sealed record CodeOriginCandidate(
    string CallerType, string CallerMethod, CodeUsage Use, int Score, IReadOnlyList<CodeOriginReason> Reasons, CodeOriginFit Fit);

/// <summary>
/// How much of a statement a candidate covers (proposal, not confirmed): of its features – each table of the model, each
/// column a table is filtered by, for DML each column it sets – those the method touches, filters by or writes. Unlike the
/// score it reads as a share; the order stays the score's.
/// </summary>
/// <param name="Missing">The features it does not cover, as reasons: a table (<see cref="CodeOriginReasonKind.Queries"/>),
/// filter columns, set columns.</param>
/// <param name="Extra">What the method has that the statement does not: filter columns (<c>TABLE.COLUMN</c>) and navigations
/// it follows in a query (<c>Entity.Navigation</c>), sorted.</param>
public sealed record CodeOriginFit(int Matched, int Total, IReadOnlyList<CodeOriginReason> Missing, IReadOnlyList<string> Extra)
{
    /// <summary>Matched of total, 0 to 1.</summary>
    public double Share => Total == 0 ? 0 : (double)Matched / Total;
}

/// <param name="Tables">The statement's tables that belong to the C# model; empty: the statement is none of the code's.</param>
/// <param name="Candidates">Best first.</param>
public sealed record CodeOriginResult(IReadOnlyList<string> Tables, IReadOnlyList<CodeOriginCandidate> Candidates)
{
    public static readonly CodeOriginResult None = new([], []);
}

/// <summary>
/// A statement read once for <see cref="CodeOriginFinder"/> (review WP-76: it was analysed twice per call, on the render
/// thread): its queries and DML statements with their filter columns and DML targets.
/// </summary>
public sealed class CodeOriginStatement
{
    internal CodeOriginStatement(string? schema, IReadOnlyList<CodeOriginFinder.Piece> pieces)
    {
        Schema = schema;
        Pieces = pieces;
    }

    public string? Schema { get; }

    internal IReadOnlyList<CodeOriginFinder.Piece> Pieces { get; }

    internal bool IsDml => Pieces.Any(p => p.Info.IsDml);
}

/// <summary>
/// The methods of the linked solution that may have sent a statement (WP-76): its tables and the columns it filters by
/// (<see cref="SqlAnalysis"/>, <see cref="SqlConditions"/>) against the code usage index (WP-40) – which methods query
/// those tables, reach them over navigations and filter by the same columns. A guess ranked by how much fits, not a
/// match: the index knows neither the order of calls nor the values. Test and mapping code are no candidates.
/// <para>Score per method (proposal, not confirmed): for a query +3 per table it queries, +2 per table it reaches over a
/// navigation in a query or (lazy loading) outside one, +1 more for a lazy load if the statement filters only by key
/// columns, +2 per column both filter by. For DML (review WP-76: a reader of the table ranked level with its writer) +3 per
/// column of the target table it writes that the statement sets, +1 per table it queries and per column both filter by
/// (loading the row before <c>SaveChanges</c>, <c>ExecuteUpdate</c>), navigations not at all. Either way −1 per table of
/// the statement it does not touch, −1 per column it filters a table of the statement by that the statement does not and
/// per navigation it follows in a query (<c>Include</c>) to a table the statement does not read – a method may hold several
/// queries, so these cost little. A method needs one table it touches and a score above zero.</para>
/// </summary>
public static class CodeOriginFinder
{
    private const int QueryScore = 3;
    private const int NavigationScore = 2;
    private const int KeyOnlyLazyScore = 1;
    private const int FilterScore = 2;
    private const int DmlQueryScore = 1;
    private const int DmlFilterScore = 1;
    private const int WriteScore = 3;
    private const int UntouchedScore = -1;
    private const int MismatchScore = -1;

    /// <summary>
    /// Characters of a statement read at most (review WP-76): the text comes from any session on the server and has no limit;
    /// its leading tables and conditions are in the first part.
    /// </summary>
    public const int MaxTextLength = 64 * 1024;

    /// <summary>A query or DML statement of the text, with what the finder needs of it.</summary>
    internal sealed record Piece(SqlStatementInfo Info, IReadOnlyList<SqlConditionColumn> Conditions, SqlDmlTarget? Target);

    /// <summary>
    /// Reads a statement once: cut to <see cref="MaxTextLength"/>, its queries and DML statements – the statement itself, or for
    /// a PL/SQL block the DML statements in it: EF Core's Oracle provider sends <c>SaveChanges</c> as
    /// <c>DECLARE … BEGIN UPDATE …; … END;</c>. A text cut short still yields its leading tables.
    /// </summary>
    /// <param name="schema">The schema the statement was parsed in (<c>PARSING_SCHEMA_NAME</c>): owner of unqualified tables.</param>
    public static CodeOriginStatement Prepare(string sql, string? schema)
    {
        var text = sql.Length > MaxTextLength ? sql[..MaxTextLength] : sql;
        return new CodeOriginStatement(schema,
            [.. PiecesOf(text).Select(p => new Piece(p.Info, SqlConditions.Of(p.Text), p.Info.IsDml ? SqlDmlTarget.Of(p.Text) : null))]);
    }

    public static CodeOriginResult Find(string sql, string? schema, CodeUsageIndex index) => Find(Prepare(sql, schema), index);

    public static CodeOriginResult Find(CodeOriginStatement statement, CodeUsageIndex index)
    {
        var tables = TablesOf(statement.Pieces, statement.Schema, index.Model);
        if (tables.Count == 0)
        {
            return CodeOriginResult.None;
        }

        foreach (var piece in statement.Pieces)
        {
            AddConditions(tables, piece.Info, piece.Conditions);
            if (piece.Target is { } target && tables.FirstOrDefault(t => t.Name == target.Table.Name) is { } written)
            {
                written.Set.UnionWith(target.Columns);
            }
        }

        var dml = statement.IsDml;
        var methods = new Dictionary<(string Type, string Method), Method>();
        foreach (var table in tables)
        {
            Collect(methods, table, tables, dml, index);
        }

        var candidates = methods.Values
            .Select(m => m.ToCandidate(tables, dml))
            .OfType<CodeOriginCandidate>()
            .OrderByDescending(c => c.Score)
            .ThenByDescending(c => c.Reasons.Count)
            .ThenBy(c => c.Use.Use.Document, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Use.Use.Line)
            .ToList();
        return new CodeOriginResult(tables.Select(t => t.Name).ToList(), candidates);
    }

    /// <summary>
    /// The statement's tables that belong to the model, without the code usage index: whether to read the solution at all
    /// (an Oracle-internal statement does not need it).
    /// </summary>
    public static IReadOnlyList<string> TablesOf(string sql, string? schema, ModelExport model) => TablesOf(Prepare(sql, schema), model);

    public static IReadOnlyList<string> TablesOf(CodeOriginStatement statement, ModelExport model) =>
        TablesOf(statement.Pieces, statement.Schema, model).Select(t => t.Name).ToList();

    private static readonly string[] DmlWords = ["INSERT", "UPDATE", "DELETE", "MERGE"];

    private static readonly string[] StatementStarts = ["BEGIN", "THEN", "ELSE", "LOOP"];

    /// <summary>
    /// The queries and DML statements to look at: the statement itself, or for a PL/SQL block the DML statements in it
    /// (each from its first word to its <c>;</c>); nothing for anything else.
    /// </summary>
    private static List<(string Text, SqlStatementInfo Info)> PiecesOf(string sql)
    {
        var info = SqlAnalysis.Analyze(sql);
        if (info.Kind == SqlStatementKind.Query || info.IsDml)
        {
            return [(sql, info)];
        }

        if (info.Kind != SqlStatementKind.PlSql)
        {
            return [];
        }

        var pieces = new List<(string, SqlStatementInfo)>();
        var tokens = SqlTokenizer.Significant(sql);
        for (var i = 1; i < tokens.Count; i++)
        {
            var before = tokens[i - 1];
            if (!(tokens[i].Kind == SqlTokenKind.Word && DmlWords.Contains(tokens[i].Value, StringComparer.OrdinalIgnoreCase))
                || !(before.IsSymbol(";") || StatementStarts.Any(before.IsWord)))
            {
                continue;
            }

            var end = i;
            var depth = 0;
            while (end + 1 < tokens.Count && !(depth == 0 && tokens[end + 1].IsSymbol(";")))
            {
                end++;
                depth += tokens[end].IsSymbol("(") ? 1 : tokens[end].IsSymbol(")") ? -1 : 0;
            }

            var text = sql[tokens[i].Start..(tokens[end].Start + tokens[end].Length)];
            if (SqlAnalysis.Analyze(text) is { IsDml: true } piece)
            {
                pieces.Add((text, piece));
            }

            i = end;
        }

        return pieces;
    }

    private static List<StatementTable> TablesOf(IReadOnlyList<Piece> pieces, string? schema, ModelExport model)
    {
        var tables = new List<StatementTable>();
        foreach (var reference in pieces.SelectMany(p => p.Info.Tables).Where(t => t.DbLink is null))
        {
            var entities = EntitiesOf(model, reference, schema);
            if (entities.Count > 0 && !tables.Any(t => t.Name == reference.Name && t.Entities.SequenceEqual(entities)))
            {
                tables.Add(new StatementTable(reference.Name, entities, [], []));
            }
        }

        return tables;
    }

    /// <summary>
    /// The entities on a table or view of the statement: by name (EF quotes, the case is exact), the owner compared where
    /// both are known – the statement's (or its parsing schema), the entity's (or the model's default schema). Owned types
    /// and derived types on the same table count: their properties are its columns.
    /// </summary>
    private static List<EntityExport> EntitiesOf(ModelExport model, SqlTableReference reference, string? schema)
    {
        var owner = reference.Owner ?? schema;
        return model.Entities
            .Where(e => Matches(e.Table, e.Schema) || Matches(e.View, e.ViewSchema))
            .ToList();

        bool Matches(string? name, string? entitySchema) =>
            name == reference.Name && ((entitySchema ?? model.DefaultSchema) is not { } expected || owner is null || expected == owner);
    }

    /// <summary>The filter columns of each table a piece names, by qualifier (alias or name) or, without one, by the column names it has.</summary>
    private static void AddConditions(List<StatementTable> tables, SqlStatementInfo info, IReadOnlyList<SqlConditionColumn> columns)
    {
        foreach (var column in columns)
        {
            foreach (var table in tables)
            {
                // The piece names the table: by alias or name before the column, or without a qualifier at all.
                var named = info.Tables.Any(t => t.Name == table.Name
                                                 && (column.Qualifier is not { } qualifier || t.Alias == qualifier || (t.Alias is null && t.Name == qualifier)));
                if (named && table.HasColumn(column.Column) && !table.Filters.Contains(column.Column))
                {
                    table.Filters.Add(column.Column);
                }
            }
        }
    }

    private static void Collect(
        Dictionary<(string, string), Method> methods, StatementTable table, List<StatementTable> tables, bool dml, CodeUsageIndex index)
    {
        foreach (var entity in table.Entities)
        {
            if (!entity.IsOwned)
            {
                foreach (var use in index.ForEntity(entity).Where(IsApplication))
                {
                    var method = MethodOf(methods, use);
                    method.Queried.Add(table.Name);
                    method.Add(use);
                }

                foreach (var use in dml ? [] : index.NavigationsTo(entity).Where(IsApplication))
                {
                    var method = MethodOf(methods, use);
                    var kind = use.Use.Kind == CodeUseKind.Read ? CodeOriginReasonKind.LazyLoads : CodeOriginReasonKind.Joins;
                    if (use.Use.Kind is CodeUseKind.Read or CodeUseKind.Expression or CodeUseKind.Filter
                        && !method.Navigations.Any(n => n.Table == table.Name && n.Kind == kind && n.Navigation == use.Navigation))
                    {
                        method.Navigations.Add((table.Name, kind, use.Navigation));
                        method.Add(use);
                    }
                }
            }

            foreach (var property in entity.Properties.Where(p => ColumnOf(p) is not null))
            {
                var column = ColumnOf(property)!;
                var matches = table.Filters.Contains(column);
                foreach (var use in index.ForProperty(entity, property.Name).Where(u => u.Use.Kind == CodeUseKind.Filter && IsApplication(u)))
                {
                    var method = MethodOf(methods, use);
                    if (matches)
                    {
                        method.Filtered.Add((table.Name, column));
                        method.Add(use);
                    }
                    else
                    {
                        method.Mismatches.Add($"{table.Name}.{column}");
                    }
                }
            }

            foreach (var (navigation, target) in NavigationsOf(index.Model, entity))
            {
                if (tables.Any(t => t.Entities.Contains(target)))
                {
                    continue;
                }

                foreach (var use in index.ForProperty(entity, navigation).Where(u => u.Use.Kind is CodeUseKind.Expression or CodeUseKind.Filter && IsApplication(u)))
                {
                    MethodOf(methods, use).Mismatches.Add($"{entity.Name}.{navigation}");
                }
            }

            if (dml && table.Set.Count > 0)
            {
                foreach (var property in entity.Properties.Where(p => ColumnOf(p) is { } c && table.Set.Contains(c)))
                {
                    foreach (var use in index.ForProperty(entity, property.Name).Where(u => u.Use.Kind == CodeUseKind.Write && IsApplication(u)))
                    {
                        var method = MethodOf(methods, use);
                        method.Written.Add((table.Name, ColumnOf(property)!));
                        method.Add(use);
                    }
                }
            }
        }
    }

    private static bool IsApplication(CodeUsage use) => !use.IsTest && !use.IsMapping;

    /// <summary>The entity's navigations with the entity they lead to: its own foreign keys' and the inverse ones of others.</summary>
    private static IEnumerable<(string Navigation, EntityExport Target)> NavigationsOf(ModelExport model, EntityExport entity)
    {
        foreach (var key in entity.ForeignKeys)
        {
            if (key.Navigation is { } navigation && model.Entities.FirstOrDefault(e => e.Name == key.PrincipalEntity) is { } principal)
            {
                yield return (navigation, principal);
            }
        }

        foreach (var dependent in model.Entities)
        {
            foreach (var key in dependent.ForeignKeys.Where(k => k.PrincipalEntity == entity.Name && k.InverseNavigation is not null))
            {
                yield return (key.InverseNavigation!, dependent);
            }
        }
    }

    private static Method MethodOf(Dictionary<(string, string), Method> methods, CodeUsage use)
    {
        var key = (use.Use.CallerType, use.Use.CallerMethod);
        if (!methods.TryGetValue(key, out var method))
        {
            methods[key] = method = new Method(use.Use.CallerType, use.Use.CallerMethod);
        }

        return method;
    }

    /// <summary>The column a property has in the statement: its table's, else its view's.</summary>
    private static string? ColumnOf(PropertyExport property) => property.Column ?? property.ViewColumn;

    /// <param name="Set">The columns a DML statement sets in this table, its target.</param>
    private sealed record StatementTable(string Name, List<EntityExport> Entities, List<string> Filters, HashSet<string> Set)
    {
        public bool HasColumn(string column) =>
            Entities.Any(e => e.Properties.Any(p => p.Column == column || p.ViewColumn == column));

        /// <summary>Every filter column is a key: the primary key or a foreign key of one of its entities (a lazy load's shape).</summary>
        public bool FiltersOnlyByKeys =>
            Filters.Count > 0 && Filters.All(column => Entities.Any(e =>
                e.Properties.Where(p => p.Column == column || p.ViewColumn == column)
                    .Any(p => e.PrimaryKey.Contains(p.Name) || e.ForeignKeys.Any(k => k.Properties.Contains(p.Name)))));
    }

    private sealed class Method(string type, string name)
    {
        public HashSet<string> Queried { get; } = [];

        public List<(string Table, CodeOriginReasonKind Kind, string? Navigation)> Navigations { get; } = [];

        public HashSet<(string Table, string Column)> Filtered { get; } = [];

        public HashSet<(string Table, string Column)> Written { get; } = [];

        /// <summary>Filter columns and navigations of the method the statement does not have.</summary>
        public HashSet<string> Mismatches { get; } = [];

        private CodeUsage? _first;

        /// <summary>Keeps the first use by line that has a file: the place shown.</summary>
        public void Add(CodeUsage use)
        {
            if (_first is null
                || (_first.Use.Document is null && use.Use.Document is not null)
                || (use.Use.Document is not null && string.Equals(use.Use.Document, _first.Use.Document, StringComparison.OrdinalIgnoreCase)
                    && Nullable.Compare(use.Use.Line, _first.Use.Line) < 0))
            {
                _first = use;
            }
        }

        public CodeOriginCandidate? ToCandidate(List<StatementTable> tables, bool dml)
        {
            var reasons = new List<CodeOriginReason>();
            var missing = new List<CodeOriginReason>();
            var score = 0;
            var touched = 0;
            var (matched, total) = (0, 0);
            foreach (var table in tables)
            {
                var touches = false;
                if (Queried.Contains(table.Name))
                {
                    reasons.Add(new CodeOriginReason(CodeOriginReasonKind.Queries, table.Name, []));
                    score += dml ? DmlQueryScore : QueryScore;
                    touches = true;
                }

                foreach (var (_, kind, navigation) in Navigations.Where(n => n.Table == table.Name))
                {
                    reasons.Add(new CodeOriginReason(kind, table.Name, [], navigation));
                    if (!touches)
                    {
                        score += NavigationScore + (kind == CodeOriginReasonKind.LazyLoads && tables.Count == 1 && table.FiltersOnlyByKeys ? KeyOnlyLazyScore : 0);
                        touches = true;
                    }
                }

                var written = Written.Where(w => w.Table == table.Name).Select(w => w.Column).ToList();
                if (written.Count > 0)
                {
                    reasons.Add(new CodeOriginReason(CodeOriginReasonKind.Writes, table.Name, written));
                    score += WriteScore * written.Count;
                    touches = true;
                }

                var filtered = table.Filters.Where(c => Filtered.Contains((table.Name, c))).ToList();
                if (filtered.Count > 0)
                {
                    reasons.Add(new CodeOriginReason(CodeOriginReasonKind.Filters, table.Name, filtered));
                    score += (dml ? DmlFilterScore : FilterScore) * filtered.Count;
                    touches = true; // a filter is a query of the entity: Where takes an Expression<T> (generic repositories have no Set<T>)
                }

                if (touches)
                {
                    touched++;
                }
                else
                {
                    score += UntouchedScore;
                    missing.Add(new CodeOriginReason(CodeOriginReasonKind.Queries, table.Name, []));
                }

                // The fit: the table, its filter columns and, for DML, the columns of the model it sets.
                var settable = dml ? table.Set.Where(table.HasColumn).Order(StringComparer.Ordinal).ToList() : [];
                matched += (touches ? 1 : 0) + filtered.Count + written.Count(settable.Contains);
                total += 1 + table.Filters.Count + settable.Count;
                if (table.Filters.Except(filtered).ToList() is { Count: > 0 } unfiltered)
                {
                    missing.Add(new CodeOriginReason(CodeOriginReasonKind.Filters, table.Name, unfiltered));
                }

                if (settable.Except(written).ToList() is { Count: > 0 } unwritten)
                {
                    missing.Add(new CodeOriginReason(CodeOriginReasonKind.Writes, table.Name, unwritten));
                }
            }

            score += MismatchScore * Mismatches.Count;
            var fit = new CodeOriginFit(matched, total, missing, [.. Mismatches.Order(StringComparer.Ordinal)]);
            return touched > 0 && score > 0 && _first is not null ? new CodeOriginCandidate(type, name, _first, score, reasons, fit) : null;
        }
    }
}
