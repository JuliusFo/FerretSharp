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

    /// <summary>The method writes properties of the table's entity (<c>SaveChanges</c>, <c>ExecuteUpdate</c>) – for DML only.</summary>
    Writes,
}

/// <param name="Table">The statement's table, as it names it.</param>
/// <param name="Columns">For <see cref="CodeOriginReasonKind.Filters"/> and <see cref="CodeOriginReasonKind.Writes"/>: the columns.</param>
/// <param name="Navigation">For joins and lazy loads: the navigation, as <c>Entity.Navigation</c>.</param>
public sealed record CodeOriginReason(CodeOriginReasonKind Kind, string Table, IReadOnlyList<string> Columns, string? Navigation = null);

/// <summary>A method that may have sent the statement, with the place to jump to and why it fits.</summary>
/// <param name="Use">The first matching use in the method (by line): its file and line are the place shown.</param>
/// <param name="Score">Higher fits better; only the order counts, not the number.</param>
public sealed record CodeOriginCandidate(string CallerType, string CallerMethod, CodeUsage Use, int Score, IReadOnlyList<CodeOriginReason> Reasons);

/// <param name="Tables">The statement's tables that belong to the C# model; empty: the statement is none of the code's.</param>
/// <param name="Candidates">Best first.</param>
public sealed record CodeOriginResult(IReadOnlyList<string> Tables, IReadOnlyList<CodeOriginCandidate> Candidates)
{
    public static readonly CodeOriginResult None = new([], []);
}

/// <summary>
/// The methods of the linked solution that may have sent a statement (WP-76): its tables and the columns it filters by
/// (<see cref="SqlAnalysis"/>, <see cref="SqlConditions"/>) against the code usage index (WP-40) – which methods query
/// those tables, reach them over navigations and filter by the same columns. A guess ranked by how much fits, not a
/// match: the index knows neither the order of calls nor the values. Test and mapping code are no candidates.
/// <para>Score per method (proposal, not confirmed): +3 per table it queries, +2 per table it reaches over a navigation in
/// a query or (lazy loading) outside one, +1 more for a lazy load if the statement filters only by key columns, +2 per
/// column both filter by, +3 per table of a DML statement whose entity it writes, −1 per table of the statement it does
/// not touch (by any of these), −1 per column it filters a table of the statement by that the statement does not and per
/// navigation it follows in a query (<c>Include</c>) to a table the statement does not read – a method may hold several
/// queries, so these cost little. A method needs one table it touches and a score above zero.</para>
/// </summary>
public static class CodeOriginFinder
{
    private const int QueryScore = 3;
    private const int NavigationScore = 2;
    private const int KeyOnlyLazyScore = 1;
    private const int FilterScore = 2;
    private const int WriteScore = 3;
    private const int UntouchedScore = -1;
    private const int MismatchScore = -1;

    /// <param name="sql">The statement's text; a text cut short still yields its leading tables.</param>
    /// <param name="schema">The schema the statement was parsed in (<c>PARSING_SCHEMA_NAME</c>): owner of unqualified tables.</param>
    public static CodeOriginResult Find(string sql, string? schema, CodeUsageIndex index)
    {
        var info = SqlAnalysis.Analyze(sql);
        if (!(info.Kind == SqlStatementKind.Query || info.IsDml))
        {
            return CodeOriginResult.None;
        }

        var tables = TablesOf(info, schema, index.Model);
        if (tables.Count == 0)
        {
            return CodeOriginResult.None;
        }

        AddConditions(tables, info, SqlConditions.Of(sql));
        var methods = new Dictionary<(string Type, string Method), Method>();
        foreach (var table in tables)
        {
            Collect(methods, table, tables, info, index);
        }

        var candidates = methods.Values
            .Select(m => m.ToCandidate(tables))
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
    public static IReadOnlyList<string> TablesOf(string sql, string? schema, ModelExport model)
    {
        var info = SqlAnalysis.Analyze(sql);
        return info.Kind == SqlStatementKind.Query || info.IsDml ? TablesOf(info, schema, model).Select(t => t.Name).ToList() : [];
    }

    private static List<StatementTable> TablesOf(SqlStatementInfo info, string? schema, ModelExport model)
    {
        var tables = new List<StatementTable>();
        foreach (var reference in info.Tables.Where(t => t.DbLink is null))
        {
            var entities = EntitiesOf(model, reference, schema);
            if (entities.Count > 0 && !tables.Any(t => t.Name == reference.Name && t.Entities.SequenceEqual(entities)))
            {
                tables.Add(new StatementTable(reference.Name, entities, []));
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

    /// <summary>The filter columns of each table, by qualifier (alias or name) or, without one, by the column names it has.</summary>
    private static void AddConditions(List<StatementTable> tables, SqlStatementInfo info, IReadOnlyList<SqlConditionColumn> columns)
    {
        foreach (var column in columns)
        {
            foreach (var table in tables)
            {
                var named = column.Qualifier is { } qualifier
                    ? info.Tables.Any(t => t.Name == table.Name && (t.Alias == qualifier || (t.Alias is null && t.Name == qualifier)))
                    : table.HasColumn(column.Column);
                if (named && table.HasColumn(column.Column) && !table.Filters.Contains(column.Column))
                {
                    table.Filters.Add(column.Column);
                }
            }
        }
    }

    private static void Collect(
        Dictionary<(string, string), Method> methods, StatementTable table, List<StatementTable> tables, SqlStatementInfo info, CodeUsageIndex index)
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

                foreach (var use in index.NavigationsTo(entity).Where(IsApplication))
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

            if (info.IsDml)
            {
                foreach (var property in entity.Properties.Where(p => ColumnOf(p) is not null))
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

    private sealed record StatementTable(string Name, List<EntityExport> Entities, List<string> Filters)
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

        public CodeOriginCandidate? ToCandidate(List<StatementTable> tables)
        {
            var reasons = new List<CodeOriginReason>();
            var score = 0;
            var touched = 0;
            foreach (var table in tables)
            {
                var touches = false;
                if (Queried.Contains(table.Name))
                {
                    reasons.Add(new CodeOriginReason(CodeOriginReasonKind.Queries, table.Name, []));
                    score += QueryScore;
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
                    score += WriteScore;
                    touches = true;
                }

                var filtered = table.Filters.Where(c => Filtered.Contains((table.Name, c))).ToList();
                if (filtered.Count > 0)
                {
                    reasons.Add(new CodeOriginReason(CodeOriginReasonKind.Filters, table.Name, filtered));
                    score += FilterScore * filtered.Count;
                    touches = true; // a filter is a query of the entity: Where takes an Expression<T> (generic repositories have no Set<T>)
                }

                if (touches)
                {
                    touched++;
                }
                else
                {
                    score += UntouchedScore;
                }
            }

            score += MismatchScore * Mismatches.Count;
            return touched > 0 && score > 0 && _first is not null ? new CodeOriginCandidate(type, name, _first, score, reasons) : null;
        }
    }
}
