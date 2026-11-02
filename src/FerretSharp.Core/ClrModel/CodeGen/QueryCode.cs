using System.Text;
using FerretSharp.Core.Query;
using FerretSharp.Core.Relations;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Schema;
using FerretSharp.Core.Settings;

namespace FerretSharp.Core.ClrModel;

/// <summary>How the C# of a query from the diagram is written (WP-69): names and style, the user's choice in the preview.</summary>
/// <param name="ClassName">The static class that holds the expressions.</param>
/// <param name="PredicateName">The expression (or method, with parameters) of the conditions of the root and its parents.</param>
/// <param name="ProjectionName">The expression that builds the DTO.</param>
/// <param name="Namespace">A file-scoped namespace for the code; null or empty for none.</param>
/// <param name="WithDto">Writes the DTO class too (off when the project has it already).</param>
/// <param name="DtoAsRecord">The DTO as <c>record</c> with <c>init</c>, else as <c>class</c> with <c>set</c>.</param>
/// <param name="LinqKit">
/// Composes with LinqKit (<c>Invoke</c>, <c>Linq.Expr(…).Expand()</c>): an expression per value and one for a chosen child.
/// Without it every value stands in the projection as a whole path.
/// </param>
/// <param name="Template">Lines for the project's own mapping instead of the projection; null for the projection.</param>
public sealed record QueryCodeOptions(
    string ClassName, string PredicateName, string ProjectionName, string DtoName, string? Namespace = null, bool WithDto = true,
    bool DtoAsRecord = true, bool LinqKit = true, QueryLineTemplate? Template = null)
{
    /// <summary>Names from the root's entity: <c>AuftragQueries</c>, <c>Filter</c>, <c>Projection</c>, <c>AuftragRow</c>.</summary>
    public static QueryCodeOptions For(string entity) => new($"{entity}Queries", "Filter", "Projection", $"{entity}Row");
}

/// <summary>
/// A project's own mapping (WP-69): a head, a line per value and a foot, with <c>{Entity}</c>, <c>{Dto}</c>,
/// <c>{Expression}</c> (the value's expression or its field) and <c>{Property}</c>. Only text is filled in.
/// </summary>
public sealed record QueryLineTemplate(string Head, string Line, string Foot);

/// <param name="Code">The static class with the expressions (and the DTO) to copy into the project.</param>
/// <param name="Usage">How to query with it: <c>db.Auftraege.Where(Filter).Select(Projection)</c>.</param>
/// <param name="Script">Code and usage for the LINQ console (no namespace, always the projection).</param>
/// <param name="SingleRead">
/// The query that reads each chosen child once (<c>SelectMany … Take(1).DefaultIfEmpty()</c>) – offered when a chosen child
/// has more than one value: with an expression per value its subquery stands in the SQL once per value; null otherwise.
/// </param>
/// <param name="Parameters">The parameters with the filters' values, to declare them in the LINQ console.</param>
/// <param name="Notes">What is left out of the code or written differently, one sentence each.</param>
public sealed record QueryCode(
    string Code, string Usage, string Script, string? SingleRead, IReadOnlyList<LinqParameter> Parameters, IReadOnlyList<string> Notes)
{
    /// <summary>
    /// The parameters as variables of the LINQ console: <c>DateTime ab = new DateTime(2026, 10, 1);</c> – typed, as
    /// <c>var nr = 5;</c> is an int a short parameter does not take (review).
    /// </summary>
    public string Variables => string.Join("\r\n", Parameters.Select(p => $"{p.Type} {CSharpCode.Identifier(p.Name)} = {p.Value};"));
}

/// <summary>
/// The C# of a query built in the relationship diagram (WP-69), in the style of hand-written query extensions: an
/// expression per value over the root entity (optional navigations with <c>!</c>, value types through them nullable), a
/// chosen child as an expression of its own that the values use through LinqKit's <c>Invoke</c>, the conditions of root
/// and parents as a predicate, and a projection into a DTO – or the project's own mapping lines. Conditions follow the
/// grid's semantics (<see cref="LinqFilter"/>). Parts without a C# form (no entity, a relationship without navigation)
/// stay out with a note; the SQL has them.
/// </summary>
public static class QueryCodeBuilder
{
    private const string Indent = "    ";

    /// <summary>Why the query has no C# at all; null if it has.</summary>
    public static string? Unavailable(QueryDesign design, ClrModelMapping? mapping) =>
        mapping?.EntityOf(design.Root) is null ? ClrModelText.NoEntity : null;

    public static QueryCode Build(QueryDesign design, ClrModelMapping mapping, Func<TableRef, TableDetails> detailsOf, QueryCodeOptions options)
    {
        if (Unavailable(design, mapping) is { } reason)
        {
            throw new InvalidOperationException(reason);
        }

        return new Writer(design, mapping, detailsOf, options).Write();
    }

    /// <summary>A C# name the user typed: letters, digits and <c>_</c>, not starting with a digit, no keyword.</summary>
    public static bool IsName(string? name) =>
        name is { Length: > 0 } && (char.IsLetter(name[0]) || name[0] == '_') && name.All(c => char.IsLetterOrDigit(c) || c == '_')
        && CSharpCode.Identifier(name) == name;

    private sealed class Writer
    {
        private static readonly HashSet<string> ValueTypes = new(StringComparer.Ordinal)
        {
            "bool", "byte", "sbyte", "short", "ushort", "int", "uint", "long", "ulong", "decimal", "double", "float", "char",
            "DateTime", "DateOnly", "TimeOnly", "DateTimeOffset", "TimeSpan", "Guid",
        };

        private readonly QueryDesign _design;
        private readonly ClrModelMapping _mapping;
        private readonly Func<TableRef, TableDetails> _detailsOf;
        private readonly QueryCodeOptions _options;
        private readonly List<string> _notes = [];
        private readonly Dictionary<QueryNode, NodeCode> _nodes = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<string> _names = new(StringComparer.Ordinal);
        private readonly string _root;
        private readonly string _rootParameter;
        private bool _filtered;

        public Writer(QueryDesign design, ClrModelMapping mapping, Func<TableRef, TableDetails> detailsOf, QueryCodeOptions options)
        {
            _design = design;
            _mapping = mapping;
            _detailsOf = detailsOf;
            _options = options with
            {
                ClassName = Checked(options.ClassName, QueryCodeOptions.For(EntityName(design.Nodes[0])).ClassName),
                PredicateName = Checked(options.PredicateName, "Filter"),
                ProjectionName = Checked(options.ProjectionName, "Projection"),
                DtoName = Checked(options.DtoName, QueryCodeOptions.For(EntityName(design.Nodes[0])).DtoName),
            };
            _root = EntityName(design.Nodes[0]);
            _rootParameter = LambdaName(_root, []);

            // A member named as its class does not compile (CS0542), a parameter named as the lambda's neither (CS0136).
            _names.UnionWith([_options.PredicateName, _options.ProjectionName, _options.ClassName, _options.DtoName, _rootParameter]);
            foreach (var node in design.Nodes)
            {
                Resolve(node);
            }
        }

        public QueryCode Write()
        {
            var values = Values();
            var (predicate, parameters) = Predicate();
            _filtered = predicate is not null;
            var helpers = _design.Nodes.Where(n => n.IsChosenChild && _nodes[n].Available && UsedByValues(n, values)).ToList();

            var members = new List<string>();
            if (_options.LinqKit)
            {
                members.AddRange(helpers.Select(Helper));
                members.AddRange(values.Where(v => !v.Node.IsRoot).Select(ValueField));
            }

            if (predicate is not null)
            {
                members.Add(predicate);
            }

            var projection = Projection(values);
            var copyMembers = new List<string>(members) { _options.Template is { } template ? Mapping(template, values) : projection };
            var scriptMembers = new List<string>(members) { projection };
            var dto = _options.WithDto ? Dto(values) : null;

            var usage = Usage(parameters);
            var code = new StringBuilder();
            if (_options.Namespace is { Length: > 0 } ns && IsNamespace(ns))
            {
                code.Append("namespace ").Append(ns).Append(";\r\n\r\n");
            }
            else if (_options.Namespace is { Length: > 0 } invalid)
            {
                Note(TextFormat.Format(ClrModelText.QueryNamespaceInvalid, invalid));
            }

            code.Append(Class(copyMembers));
            if (dto is not null)
            {
                code.Append("\r\n\r\n").Append(dto);
            }

            // A script has no project setting for nullable reference types: without it, string? warns (CS8632, E2E).
            var script = new StringBuilder(Usings()).Append("\r\n#nullable enable\r\n\r\n").Append(Class(scriptMembers));
            if (dto is not null)
            {
                script.Append("\r\n\r\n").Append(dto);
            }

            script.Append("\r\n\r\n").Append(usage);
            var singleRead = helpers.Any(h => values.Count(v => ReferenceEquals(ZoneOf(v.Node), h)) > 1) ? SingleRead(values, parameters) : null;
            return new QueryCode(Usings() + "\r\n" + code, usage, script.ToString(), singleRead, parameters, _notes);
        }

        private string Usings() =>
            "using System.Linq.Expressions;\r\nusing Microsoft.EntityFrameworkCore;\r\n" + (_options.LinqKit ? "using LinqKit;\r\n" : "");

        private string Class(IEnumerable<string> members) =>
            $"public static class {_options.ClassName}\r\n{{\r\n{string.Join("\r\n\r\n", members.Select(m => Indented(m, 1)))}\r\n}}";

        // --- What each node is in C# ---------------------------------------------------------------------------------

        /// <param name="Navigation">The navigation of the step: on the referencing entity to a parent, on the parent to its children.</param>
        /// <param name="Required">A parent through FK properties that are not nullable: no <c>!</c>, values keep their type.</param>
        private sealed record NodeCode(bool Available, EntityMapping? Entity, TablePresentation? Presentation, string? Navigation, bool Required, string Name);

        private void Resolve(QueryNode node)
        {
            var details = _detailsOf(node.Table);
            if (node.From is { } from && !_nodes[from].Available)
            {
                _nodes[node] = new NodeCode(false, null, null, null, false, "");
                return;
            }

            var entity = _mapping.EntityOf(node.Table);
            var presentation = entity is null ? null : TablePresentation.Create(details, _mapping, ClrNameDisplay.Front);
            if (entity is null)
            {
                Note(TextFormat.Format(ClrModelText.QueryNodeNoEntity, QueryDesign.Label(node)));
                _nodes[node] = new NodeCode(false, null, null, null, false, "");
                return;
            }

            if (node.From is null)
            {
                _nodes[node] = new NodeCode(true, entity, presentation, null, true, "");
                return;
            }

            var fromEntity = _nodes[node.From].Entity!;
            var relation = node.Relation!;
            var (dependent, principal) = node.IsChosenChild ? (entity, fromEntity) : (fromEntity, entity);
            var key = dependent.Entity.ForeignKeys.FirstOrDefault(fk =>
                fk.PrincipalEntity == principal.Entity.Name
                && fk.Properties.Select(p => ColumnOf(dependent, p)).SequenceEqual(relation.FromColumns, StringComparer.Ordinal));
            var navigation = node.IsChosenChild ? key is { IsUnique: false } ? key.InverseNavigation : null : key?.Navigation;
            if (navigation is null)
            {
                Note(TextFormat.Format(ClrModelText.QueryNodeNoNavigation, QueryDesign.Label(node), relation.Name));
                _nodes[node] = new NodeCode(false, null, null, null, false, "");
                return;
            }

            var required = !node.IsChosenChild && key!.Properties.All(p => dependent.Entity.Properties.FirstOrDefault(e => e.Name == p) is { Nullable: false });
            var prefix = node.IsChosenChild ? $"First{EntityName(node)}" : navigation;
            var name = node.From is { IsRoot: false } f ? _nodes[f].Name + prefix : prefix;
            if (node.IsChosenChild)
            {
                name = Unique(name); // the name of its own expression
            }
            _nodes[node] = new NodeCode(true, entity, presentation, navigation, required, name);
        }

        private static string? ColumnOf(EntityMapping entity, string property) => entity.Properties.FirstOrDefault(p => p.Value.Name == property).Key;

        private string EntityName(QueryNode node) =>
            _mapping.EntityOf(node.Table) is { } entity ? ClrModelMapping.ShortName(entity.Entity.ClrType) : node.Table.Name;

        /// <summary>The root or the chosen child whose part the node is in (<see cref="QueryBuilder.PartOf"/>).</summary>
        private static QueryNode ZoneOf(QueryNode node) => QueryBuilder.PartOf(node);

        /// <summary>
        /// The C# of a node: the access from <paramref name="anchor"/> on (it knows the root and chosen children of the
        /// context) through the navigations to parents, and whether it may be null.
        /// </summary>
        private (string Text, bool Nullable) MemberOf(QueryNode node, Func<QueryNode, (string Text, bool Nullable)?> anchor)
        {
            if (anchor(node) is { } own)
            {
                return own;
            }

            if (node.From is null || node.IsChosenChild)
            {
                throw new InvalidOperationException("The anchor knows the root and every chosen child.");
            }

            var (from, nullable) = MemberOf(node.From, anchor);
            var code = _nodes[node];
            return ($"{from}{(nullable ? "!" : "")}.{CSharpCode.Identifier(code.Navigation!)}", nullable || !code.Required);
        }

        /// <summary>The anchor of an expression over the root: a chosen child through its own expression (LinqKit) or its whole path.</summary>
        private (string Text, bool Nullable)? RootAnchor(QueryNode node) =>
            node.IsRoot ? (_rootParameter, false)
            : node.IsChosenChild ? (_options.LinqKit ? $"{HelperName(node)}.Invoke({_rootParameter})" : ChildPath(node, RootAnchor), true)
            : null;

        // --- A chosen child ---------------------------------------------------------------------------------------------

        private string HelperName(QueryNode child) => _nodes[child].Name;

        /// <summary><c>a.Positionen.Where(p => …).OrderBy(p => p.PosNr).FirstOrDefault()</c>: the chosen child from its node.</summary>
        /// <param name="taken">Lambda parameters of the context, which the child's must not shadow.</param>
        private string ChildPath(QueryNode child, Func<QueryNode, (string Text, bool Nullable)?> anchor, IReadOnlyCollection<string>? taken = null)
        {
            var (from, nullable) = MemberOf(child.From!, anchor);
            var code = _nodes[child];
            var lambda = LambdaName(code.Navigation!, [_rootParameter, .. taken ?? []]);
            var text = new StringBuilder(from).Append(nullable ? "!" : "").Append('.').Append(CSharpCode.Identifier(code.Navigation!));

            var zone = _design.Nodes.Where(n => ReferenceEquals(ZoneOf(n), child) && _nodes[n].Available).ToList();
            var conditions = zone.SelectMany(n => Conditions(n, n2 => ReferenceEquals(n2, child) ? (lambda, false) : null, parameters: false).Conditions).ToList();
            if (conditions.Count > 0)
            {
                text.Append($".Where({lambda} => ").AppendJoin(" && ", conditions).Append(')');
            }

            var order = Order(child, lambda);
            for (var i = 0; i < order.Count; i++)
            {
                var (member, descending) = order[i];
                text.Append(i == 0 ? descending ? ".OrderByDescending(" : ".OrderBy(" : descending ? ".ThenByDescending(" : ".ThenBy(")
                    .Append(lambda).Append(" => ").Append(member).Append(')');
            }

            return text.Append(".FirstOrDefault()").ToString();
        }

        /// <summary>The child's order, then its primary key (the first must be one row, not any).</summary>
        private List<(string Member, bool Descending)> Order(QueryNode child, string lambda)
        {
            var code = _nodes[child];
            var order = new List<(string, bool)>();
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (var sort in child.State.Sorts)
            {
                if (code.Entity!.Properties.GetValueOrDefault(sort.Column) is { } property && used.Add(property.Name))
                {
                    order.Add((LinqFilter.Member(lambda, property), sort.Descending));
                }
            }

            foreach (var key in code.Entity!.Entity.PrimaryKey.Where(used.Add))
            {
                if (code.Entity.Entity.Properties.FirstOrDefault(p => p.Name == key) is { } property)
                {
                    order.Add((LinqFilter.Member(lambda, property), false));
                }
            }

            return order;
        }

        private string Helper(QueryNode child)
        {
            var body = ChildPath(child, RootAnchor);
            var type = $"Expression<Func<{_root}, {EntityName(child)}?>>";
            return body.Contains(".Invoke(", StringComparison.Ordinal)
                ? $"private static readonly {type} {HelperName(child)} =\r\n{Indent}Linq.Expr(({_root} {_rootParameter}) => {body}).Expand();"
                : $"private static readonly {type} {HelperName(child)} = {_rootParameter} =>\r\n{Indent}{body};";
        }

        private static bool UsedByValues(QueryNode child, IReadOnlyList<Value> values) =>
            values.Any(v => ReferenceEquals(ZoneOf(v.Node), child) || IsBelow(v.Node, child));

        private static bool IsBelow(QueryNode node, QueryNode above)
        {
            for (var n = node.From; n is not null; n = n.From)
            {
                if (ReferenceEquals(n, above))
                {
                    return true;
                }
            }

            return false;
        }

        // --- Values -------------------------------------------------------------------------------------------------------

        private sealed record Value(QueryNode Node, PropertyExport Property, string Name, string Type, bool Cast);

        private List<Value> Values()
        {
            var values = new List<Value>();
            foreach (var node in _design.Nodes.Where(n => _nodes[n].Available))
            {
                var code = _nodes[node];
                foreach (var column in node.State.Columns)
                {
                    var index = code.Presentation!.Details.IndexOf(column.Column);
                    if (index < 0 || code.Presentation.PropertyOf(index) is not { } property || !code.Presentation.IsEntityProperty(index))
                    {
                        Note(TextFormat.Format(ClrModelText.QueryValueNoProperty, QueryDesign.Label(node), column.Column));
                        continue;
                    }

                    var optional = !node.IsRoot && MemberOf(node, RootAnchor).Nullable;
                    var valueType = ValueTypes.Contains(property.ClrType) || property.Values is not null;
                    var type = TablePresentation.ClrTypeText(property);
                    var cast = false;
                    if (optional && !type.EndsWith('?'))
                    {
                        type += "?";
                        cast = valueType;
                    }

                    var own = column.Name is { Length: > 0 } given && IsName(given) ? given : null;
                    if (column.Name is { Length: > 0 } invalid && own is null)
                    {
                        Note(TextFormat.Format(ClrModelText.QueryNameInvalid, invalid, code.Name + property.Name));
                    }

                    values.Add(new Value(node, property, Unique(own ?? code.Name + property.Name), type, cast));
                }
            }

            return values;
        }

        /// <summary>The value's expression over the root: <c>a.Kunde!.Name</c>, <c>(int?)a.Bearbeiter!.Nr</c>.</summary>
        private string ValueBody(Value value, Func<QueryNode, (string Text, bool Nullable)?> anchor)
        {
            var (target, nullable) = MemberOf(value.Node, anchor);
            var member = LinqFilter.Member(nullable ? target + "!" : target, value.Property);
            return value.Cast ? $"({value.Type}){member}" : member;
        }

        private string ValueField(Value value)
        {
            var body = ValueBody(value, RootAnchor);
            var type = $"Expression<Func<{_root}, {value.Type}>>";
            return body.Contains(".Invoke(", StringComparison.Ordinal)
                ? $"internal static readonly {type} {value.Name} =\r\n{Indent}Linq.Expr(({_root} {_rootParameter}) => {body}).Expand();"
                : $"internal static readonly {type} {value.Name} = {_rootParameter} => {body};";
        }

        /// <summary>A value in the projection: a root column directly, else its field through <c>Invoke</c>, without LinqKit its path.</summary>
        private string InProjection(Value value) =>
            value.Node.IsRoot ? ValueBody(value, RootAnchor)
            : _options.LinqKit ? $"{value.Name}.Invoke({_rootParameter})"
            : ValueBody(value, RootAnchor);

        private string Projection(IReadOnlyList<Value> values)
        {
            var type = $"Expression<Func<{_root}, {_options.DtoName}>>";
            var init = $"new {_options.DtoName}\r\n{{\r\n{string.Join("\r\n", values.Select(v => $"{Indent}{v.Name} = {InProjection(v)},"))}\r\n}}";
            return _options.LinqKit && values.Any(v => !v.Node.IsRoot)
                ? $"public static readonly {type} {_options.ProjectionName} =\r\n{Indent}Linq.Expr(({_root} {_rootParameter}) => {Indented(init, 1).TrimStart()}).Expand();"
                : $"public static readonly {type} {_options.ProjectionName} = {_rootParameter} => {Indented(init, 1).TrimStart()};";
        }

        /// <summary>The project's own mapping: the template's head, a line per value, its foot – text filled in, nothing more.</summary>
        private string Mapping(QueryLineTemplate template, IReadOnlyList<Value> values)
        {
            string Fill(string text, Value? value) => text
                .Replace("{Entity}", _root, StringComparison.Ordinal)
                .Replace("{Dto}", _options.DtoName, StringComparison.Ordinal)
                .Replace("{Expression}", value is null ? "" : value.Node.IsRoot || !_options.LinqKit ? $"{_rootParameter} => {ValueBody(value, RootAnchor)}" : value.Name, StringComparison.Ordinal)
                .Replace("{Property}", value?.Name ?? "", StringComparison.Ordinal);

            var lines = new List<string> { Fill(template.Head, null) };
            lines.AddRange(values.Select(v => Indent + Fill(template.Line, v)));
            lines.Add(Fill(template.Foot, null));
            return string.Join("\r\n", lines.Where(l => l.Trim().Length > 0));
        }

        private string Dto(IReadOnlyList<Value> values)
        {
            var accessor = _options.DtoAsRecord ? "init" : "set";
            var properties = values.Select(v =>
            {
                var initializer = v.Type == "string" ? " = \"\";" : !v.Type.EndsWith('?') && !ValueTypes.Contains(v.Type) && v.Property.Values is null ? " = null!;" : "";
                return $"{Indent}public {v.Type} {v.Name} {{ get; {accessor}; }}{initializer}";
            });
            return $"public sealed {(_options.DtoAsRecord ? "record" : "class")} {_options.DtoName}\r\n{{\r\n{string.Join("\r\n\r\n", properties)}\r\n}}";
        }

        // --- Conditions ---------------------------------------------------------------------------------------------------

        private LinqTerms Conditions(QueryNode node, Func<QueryNode, (string Text, bool Nullable)?> anchor, bool parameters)
        {
            var code = _nodes[node];
            var (target, nullable) = MemberOf(node, anchor);
            var filters = node.State.Filters;
            IReadOnlyList<string?>? names = null;
            if (parameters)
            {
                names = [.. filters.Select(f => f.Parameter is { } p && IsName(p) ? p : null)];
                foreach (var invalid in filters.Select(f => f.Parameter).Where(p => p is not null && !IsName(p)))
                {
                    Note(TextFormat.Format(ClrModelText.QueryParameterNameInvalid, QueryDesign.Label(node), invalid));
                }
            }
            else if (filters.Any(f => f.Parameter is not null))
            {
                Note(TextFormat.Format(ClrModelText.QueryParameterInChild, QueryDesign.Label(node)));
            }

            var terms = LinqFilter.Terms(code.Presentation!, [.. filters.Select(f => f.Condition)],
                property => LinqFilter.Member(nullable ? target + "!" : target, property), nullable ? target : null, names, parameters ? Unique : null);
            foreach (var skipped in terms.Skipped)
            {
                Note(TextFormat.Format(QueryText.DesignNodeProblem, QueryDesign.Label(node), TextFormat.Format(ClrModelText.FilterSkipped, skipped)));
            }

            return terms;
        }

        /// <summary>The conditions of the root and its parents as the predicate – a method when some are parameters.</summary>
        private (string? Code, IReadOnlyList<LinqParameter> Parameters) Predicate()
        {
            var conditions = new List<string>();
            var parameters = new List<LinqParameter>();
            foreach (var node in _design.Nodes.Where(n => _nodes[n].Available && ZoneOf(n).IsRoot))
            {
                var terms = Conditions(node, RootAnchor, parameters: true);
                conditions.AddRange(terms.Conditions);
                parameters.AddRange(terms.Parameters);
            }

            if (conditions.Count == 0)
            {
                return (null, parameters);
            }

            var type = $"Expression<Func<{_root}, bool>>";
            var body = string.Join($"\r\n{Indent}&& ", conditions);
            return parameters.Count == 0
                ? ($"public static readonly {type} {_options.PredicateName} = {_rootParameter} =>\r\n{Indent}{body};", parameters)
                : ($"public static {type} {_options.PredicateName}({string.Join(", ", parameters.Select(p => $"{p.Type} {CSharpCode.Identifier(p.Name)}"))}) => {_rootParameter} =>\r\n{Indent}{body};", parameters);
        }

        // --- Usage --------------------------------------------------------------------------------------------------------

        private string Source() => LinqFilter.Source(_nodes[_design.Nodes[0]].Entity!.Entity);

        /// <summary>The filter where there is one – not where every condition was left out (review: CS0117).</summary>
        private string Where(IReadOnlyList<LinqParameter> parameters) =>
            !_filtered ? ""
            : parameters.Count == 0 ? $"\r\n{Indent}.Where({_options.ClassName}.{_options.PredicateName})"
            : $"\r\n{Indent}.Where({_options.ClassName}.{_options.PredicateName}({string.Join(", ", parameters.Select(p => CSharpCode.Identifier(p.Name)))}))";

        private string Usage(IReadOnlyList<LinqParameter> parameters) =>
            $"{Source()}{Where(parameters)}\r\n{Indent}.Select({_options.ClassName}.{_options.ProjectionName})";

        /// <summary>
        /// Every chosen child through <c>SelectMany(… .Take(1).DefaultIfEmpty())</c>: one join per child that all its values
        /// read from (measured 2026-11-02; the expressions per value put a subquery per value into the SQL).
        /// </summary>
        private string SingleRead(IReadOnlyList<Value> values, IReadOnlyList<LinqParameter> parameters)
        {
            var children = _design.Nodes.Where(n => n.IsChosenChild && _nodes[n].Available && UsedByValues(n, values)).ToList();
            var ranges = new List<(QueryNode Node, string Name)> { (_design.Nodes[0], _rootParameter) };
            var text = new StringBuilder(Source()).Append(Where(parameters));
            var taken = new List<string> { _rootParameter, "x" };
            foreach (var child in children)
            {
                var name = LambdaName(_nodes[child].Navigation!, taken);
                var current = ranges.ToList();
                string Range(string n) => current.Count == 1 ? n : $"x.{n}";
                var path = ChildPath(child, n => current.FirstOrDefault(r => ReferenceEquals(r.Node, n)) is { Name: { } r } ? (Range(r), !ReferenceEquals(n, _design.Nodes[0])) : null, taken);
                path = path[..^".FirstOrDefault()".Length] + ".Take(1).DefaultIfEmpty()";
                taken.Add(name);
                var outer = current.Count == 1 ? current[0].Name : "x";
                var carried = current.Count == 1 ? current[0].Name : string.Join(", ", current.Select(r => $"x.{r.Name}"));
                text.Append($"\r\n{Indent}.SelectMany({outer} => {path}, ({outer}, {name}) => new {{ {carried}, {name} }})");
                ranges.Add((child, name));
            }

            (string Text, bool Nullable)? Anchor(QueryNode node) =>
                ranges.FirstOrDefault(r => ReferenceEquals(r.Node, node)) is { Name: { } r } ? ($"x.{r}", !node.IsRoot) : null;
            var init = string.Join("\r\n", values.Select(v => $"{Indent}{v.Name} = {ValueBody(v, Anchor)},"));
            return text.Append($"\r\n{Indent}.Select(x => new {_options.DtoName}\r\n{Indent}{{\r\n{Indented(init, 1)}\r\n{Indent}}})").ToString();
        }

        // --- Names --------------------------------------------------------------------------------------------------------

        private string Checked(string name, string fallback)
        {
            if (IsName(name))
            {
                return name;
            }

            _notes.Add(TextFormat.Format(ClrModelText.QueryNameInvalid, name, fallback));
            return fallback;
        }

        private string Unique(string name)
        {
            var unique = name;
            for (var n = 2; !_names.Add(unique); n++)
            {
                unique = name + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            return unique;
        }

        /// <summary>The lambda parameter of an entity: its first letter in lower case, a digit added if it is taken.</summary>
        private static string LambdaName(string entity, IReadOnlyCollection<string> taken)
        {
            var name = CSharpCode.LocalName(entity.Length > 0 ? entity[..1] : "x");
            var unique = name;
            for (var n = 2; taken.Contains(unique); n++)
            {
                unique = name + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            return unique;
        }

        private static bool IsNamespace(string name) => name.Split('.').All(IsName);

        private void Note(string text)
        {
            if (!_notes.Contains(text))
            {
                _notes.Add(text);
            }
        }

        private static string Indented(string text, int levels)
        {
            var indent = string.Concat(Enumerable.Repeat(Indent, levels));
            return indent + text.Replace("\r\n", "\r\n" + indent, StringComparison.Ordinal);
        }
    }
}
