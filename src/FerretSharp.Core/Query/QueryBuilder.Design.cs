using System.Text;
using FerretSharp.Core.Oracle;
using FerretSharp.Core.Relations;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Query;

/// <summary>What the SQL of a query from the diagram (WP-69) is for.</summary>
public enum DesignSqlMode
{
    /// <summary>To run (preview, SQL editor, copy): values as binds, ordered by the root's row key so pages are stable.</summary>
    Statement,

    /// <summary>The body of a view: values as literals (a view has no binds), no order, conditions "als Parameter" left out.</summary>
    View,
}

/// <param name="Columns">The result columns in the order of the select list.</param>
/// <param name="LeftOut">Conditions of a view that are parameters in C# – a view cannot have them (in their groups, WP-50).</param>
public sealed record DesignSql(
    string Sql, IReadOnlyList<QueryParameter> Parameters, IReadOnlyList<DesignSqlColumn> Columns, IReadOnlyList<(QueryNode Node, QueryFilter Filter)> LeftOut);

/// <param name="Name">The column's name in the result (its alias), unique in the statement.</param>
public sealed record DesignSqlColumn(QueryNode Node, ColumnInfo Column, string Name);

public static partial class QueryBuilder
{
    /// <summary>
    /// The query built in the relationship diagram as one statement. The root is the FROM table; a parent comes in by a
    /// join – INNER only where no row of the root can get lost (<see cref="JoinsInner"/>), LEFT otherwise. A chosen child
    /// is an <c>OUTER APPLY (… FETCH FIRST 1 ROW ONLY)</c>: its conditions and those of the parents reached from it choose
    /// the child, its order (then its row key) decides which one is first. Conditions of the root and its parents limit
    /// the root's rows – with the grid's NULL rules, so a row without a parent stays where the operator takes NULL in.
    /// </summary>
    /// <param name="detailsOf">The columns of every table of the query (all loaded by the caller).</param>
    public static DesignSql BuildDesign(QueryDesign design, Func<TableRef, TableDetails> detailsOf, DesignSqlMode mode)
    {
        var writer = new DesignWriter(design, detailsOf, mode);
        return writer.Write();
    }

    /// <summary>
    /// Whether the step to <paramref name="node"/> joins INNER: a parent through a constraint that is enabled and validated
    /// (<see cref="ForeignKeyInfo.Enforced"/>), whose columns are NOT NULL, from a node that is inner itself – after a LEFT
    /// JOIN an INNER one would drop the rows without the first parent. The root and a chosen child are the rows of their part.
    /// </summary>
    public static bool JoinsInner(QueryNode node, Func<TableRef, TableDetails> detailsOf)
    {
        if (node.From is null || node.IsChosenChild)
        {
            return true;
        }

        var from = detailsOf(node.From.Table);
        return node.Relation!.Enforced
               && node.Relation.FromColumns.All(c => from.Columns.FirstOrDefault(column => column.Name == c) is { Nullable: false })
               && JoinsInner(node.From, detailsOf);
    }

    /// <summary>
    /// The chosen child whose conditions a node's conditions belong to, or the root: the nearest of them on its way back.
    /// A query from the diagram falls into such parts – the root with its parents, each chosen child with its parents.
    /// </summary>
    public static QueryNode PartOf(QueryNode node)
    {
        var part = node;
        while (part.From is not null && !part.IsChosenChild)
        {
            part = part.From;
        }

        return part;
    }

    /// <summary>Names of the result columns, unique in the statement and usable in a view.</summary>
    public static IReadOnlyDictionary<(int Node, string Column), string> DesignColumnNames(QueryDesign design)
    {
        var names = new Dictionary<(int, string), string>();
        var taken = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in design.Nodes)
        {
            foreach (var column in node.State.Columns)
            {
                var name = column.Name is { Length: > 0 } own ? UpperSnake(own)
                    : node.IsRoot ? column.Column
                    : $"{Prefix(node)}_{column.Column}";
                name = OracleIdentifier.Cut(name, OracleIdentifier.MaxBytes);
                var unique = name;
                for (var n = 2; !taken.Add(unique); n++)
                {
                    var suffix = "_" + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    unique = OracleIdentifier.Cut(name, OracleIdentifier.MaxBytes - suffix.Length) + suffix;
                }

                names[(node.Index, column.Column)] = unique;
            }
        }

        return names;
    }

    /// <summary>
    /// "RE_KUNDE" for the parent through <c>RE_KUNDE_ID</c>, "POSITION" for a chosen child, joined along the path
    /// (<c>RE_KUNDE_LAND</c>): what a column of the node is called after.
    /// </summary>
    private static string Prefix(QueryNode node)
    {
        var own = node.IsChosenChild ? node.Table.Name
            : node.Relation!.FromColumns is [var single] && single.EndsWith("_ID", StringComparison.Ordinal) && single.Length > 3 ? single[..^3]
            : node.Table.Name;
        return node.From is { IsRoot: false } from ? $"{Prefix(from)}_{own}" : own;
    }

    /// <summary><c>KundeName</c> → <c>KUNDE_NAME</c>: a name the user gave a value, as a column name.</summary>
    private static string UpperSnake(string name)
    {
        var text = new StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c) && i > 0 && (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1])))
            {
                text.Append('_');
            }

            text.Append(char.IsAsciiLetterOrDigit(c) || c is '_' or '$' or '#' ? char.ToUpperInvariant(c) : '_');
        }

        return text.Length > 0 && char.IsAsciiLetter(text[0]) ? text.ToString() : "C_" + text;
    }

    private sealed class DesignWriter(QueryDesign design, Func<TableRef, TableDetails> detailsOf, DesignSqlMode mode)
    {
        private readonly List<QueryParameter> _parameters = [];
        private readonly List<(QueryNode, QueryFilter)> _leftOut = [];
        private readonly IReadOnlyDictionary<(int Node, string Column), string> _names = DesignColumnNames(design);

        private bool Literals => mode == DesignSqlMode.View;

        public DesignSql Write()
        {
            var root = design.Nodes[0];
            var part = Part(root);
            if (part.Exposed.Count == 0)
            {
                throw new InvalidOperationException("The query has no column.");
            }

            var sql = new StringBuilder("SELECT ")
                .AppendJoin(",\n       ", part.Exposed.Select(e => $"{e.Ref} {OracleIdentifier.Quote(e.Name)}"))
                .Append(part.From)
                .Append(part.Conditions.Count == 0 ? "" : "\n WHERE " + string.Join("\n   AND ", part.Conditions));
            if (mode == DesignSqlMode.Statement && RowKeyOrder(root) is { Count: > 0 } order)
            {
                sql.Append("\n ORDER BY ").AppendJoin(", ", order);
            }

            var columns = part.Exposed.Select(e => new DesignSqlColumn(e.Node, e.Column, e.Name)).ToList();
            return new DesignSql(sql.ToString(), _parameters, columns, _leftOut);
        }

        /// <summary>
        /// A part of the query: <paramref name="top"/> (the root or a chosen child) and its parents – FROM with the joins and
        /// the applies of the chosen children reached from them, the conditions, and the columns it hands out.
        /// </summary>
        private (string From, List<string> Conditions, List<(QueryNode Node, ColumnInfo Column, string Name, string Ref)> Exposed) Part(QueryNode top)
        {
            var members = design.Nodes.Where(n => ReferenceEquals(PartOf(n), top)).ToList();
            var from = new StringBuilder("\n  FROM ").Append(Table(top)).Append(' ').Append(AliasOf(top));
            var conditions = new List<string>();
            var exposed = new List<(QueryNode, ColumnInfo, string, string)>();

            foreach (var node in members)
            {
                var details = detailsOf(node.Table);
                if (!ReferenceEquals(node, top))
                {
                    from.Append(JoinsInner(node, detailsOf) ? "\n  JOIN " : "\n  LEFT JOIN ")
                        .Append(Table(node)).Append(' ').Append(AliasOf(node))
                        .Append(" ON ").Append(Link(node));
                }

                // After a LEFT JOIN any column of the node can be NULL: "≠" and NOT groups must take that row in, as in C#.
                var conditionTable = JoinsInner(node, detailsOf) ? details : details with { Columns = [.. details.Columns.Select(c => c with { Nullable = true })] };
                var builder = new Builder(conditionTable, AliasOf(node), _parameters, Literals);
                conditions.AddRange(Validated(node, () => builder.Conditions(Filters(node))));
                foreach (var column in node.State.Columns)
                {
                    var info = details.Columns.First(c => c.Name == column.Column);
                    if (ProjectionOf(info) != Projection.Value)
                    {
                        throw new InvalidOperationException($"{info.Name} is a {info.DataType}: it cannot be a column of the query.");
                    }

                    exposed.Add((node, info, _names[(node.Index, column.Column)], builder.Ref(info)));
                }
            }

            foreach (var child in design.Nodes.Where(n => n.IsChosenChild && members.Any(m => ReferenceEquals(m, n.From))))
            {
                var inner = Part(child);
                if (inner.Exposed.Count == 0)
                {
                    // An outer apply without columns changes no row of the root: left out.
                    continue;
                }

                from.Append("\n  OUTER APPLY (").Append(Indented(ChildSelect(child, inner))).Append(") ").Append(AliasOf(child));
                exposed.AddRange(inner.Exposed.Select(e => (e.Node, e.Column, e.Name, $"{AliasOf(child)}.{OracleIdentifier.Quote(e.Name)}")));
            }

            return (from.ToString(), conditions, exposed);
        }

        /// <summary>The select of a chosen child: its part, tied to the node it is chosen from, the first by its order.</summary>
        private string ChildSelect(QueryNode child, (string From, List<string> Conditions, List<(QueryNode Node, ColumnInfo Column, string Name, string Ref)> Exposed) part)
        {
            var conditions = new List<string> { Link(child) };
            conditions.AddRange(part.Conditions);
            var details = detailsOf(child.Table);
            var builder = new Builder(details, AliasOf(child), _parameters, Literals);
            var order = Validated(child, () => builder.OrderBy(child.State.Sorts, keylessViewByAllColumns: false).ToList());
            var sql = new StringBuilder("SELECT ")
                .AppendJoin(", ", part.Exposed.Select(e => $"{e.Ref} {OracleIdentifier.Quote(e.Name)}"))
                .Append(part.From)
                .Append("\n WHERE ").AppendJoin("\n   AND ", conditions);
            if (order.Count > 0)
            {
                sql.Append("\n ORDER BY ").AppendJoin(", ", order);
            }

            return sql.Append("\n FETCH FIRST 1 ROW ONLY").ToString();
        }

        /// <summary>The join condition of a node with the node it is reached from.</summary>
        private string Link(QueryNode node)
        {
            var relation = node.Relation!;
            var (childAlias, parentAlias) = node.IsChosenChild ? (AliasOf(node), AliasOf(node.From!)) : (AliasOf(node.From!), AliasOf(node));
            return string.Join(" AND ", relation.FromColumns.Select((c, i) =>
                $"{childAlias}.{OracleIdentifier.Quote(c)} = {parentAlias}.{OracleIdentifier.Quote(relation.ToColumns[i])}"));
        }

        /// <summary>The conditions of a node that go into the statement: a view leaves the parameters out, with their groups.</summary>
        private List<FilterCondition> Filters(QueryNode node)
        {
            var filters = node.State.Filters;
            if (mode != DesignSqlMode.View || filters.All(f => f.Parameter is null))
            {
                return [.. filters.Select(f => f.Condition)];
            }

            var groupsOut = filters.Where(f => f.Parameter is not null && f.Condition.Group is not null).Select(f => f.Condition.Group!.Id).ToHashSet();
            var kept = new List<FilterCondition>();
            foreach (var filter in filters)
            {
                if (filter.Parameter is not null || filter.Condition.Group is { } group && groupsOut.Contains(group.Id))
                {
                    _leftOut.Add((node, filter));
                }
                else
                {
                    kept.Add(filter.Condition);
                }
            }

            return kept;
        }

        private List<string> RowKeyOrder(QueryNode root)
        {
            var details = detailsOf(root.Table);
            var alias = AliasOf(root);
            return RowKeyOf(details) switch
            {
                RowKeyKind.PrimaryKey => [.. details.PrimaryKey.Select(c => $"{alias}.{OracleIdentifier.Quote(c)}")],
                RowKeyKind.RowId => [$"{alias}.ROWID"],
                _ => [],
            };
        }

        private static T Validated<T>(QueryNode node, Func<T> build)
        {
            try
            {
                return build();
            }
            catch (QueryValidationException e)
            {
                throw new QueryValidationException(e.Errors.ToDictionary(
                    p => p.Key, p => TextFormat.Format(QueryText.DesignNodeProblem, QueryDesign.Label(node), p.Value)));
            }
        }

        private static string Table(QueryNode node) => OracleIdentifier.Qualify(node.Table.Owner, node.Table.Name);

        private static string AliasOf(QueryNode node) => "t" + node.Index.ToString(System.Globalization.CultureInfo.InvariantCulture);

        private static string Indented(string sql) => sql.Replace("\n", "\n        ", StringComparison.Ordinal);
    }
}
