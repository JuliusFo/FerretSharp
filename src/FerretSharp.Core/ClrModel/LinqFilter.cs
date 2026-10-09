using FerretSharp.Core.Data;
using FerretSharp.Core.Query;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.ClrModel;

/// <summary>
/// The filters of a tab as a LINQ <c>Where</c> over the entity (WP-15): property names instead of columns, enum members
/// and true/false instead of database values. The expression finds the same rows as the grid (decision of the user):
/// text searches ignore case through <c>ToUpper()</c>, a date without time means the whole day, "≠" includes NULL as
/// C# and EF do. Filters without a C# form (no property, no member for the value) stay out as comments.
/// </summary>
public static class LinqFilter
{
    private const string Parameter = "x";

    /// <summary>Why a tab cannot be turned into C#; null if it can.</summary>
    public static string? Unavailable(TablePresentation presentation) =>
        presentation.Entity is null ? ClrModelText.NoEntity : null;

    /// <summary>
    /// The query source for the LINQ console: <c>db.Kunden</c>, or <c>db.Set&lt;Kunde&gt;()</c> without a DbSet property.
    /// </summary>
    public static string Source(TablePresentation presentation, string context = "db")
    {
        var entity = presentation.Entity ?? throw new InvalidOperationException("The table has no entity.");
        return entity.Entity.DbSet is { } dbSet
            ? $"{context}.{CSharpCode.Identifier(dbSet)}"
            : $"{context}.Set<{ClrModelMapping.ShortName(entity.Entity.ClrType)}>()";
    }

    /// <summary>
    /// <c>.Where(x => …)</c> for the enabled filters, or – with <paramref name="source"/> – a whole query
    /// (<c>db.Kunden.Where(…)</c>). <see cref="ExportText.Rows"/> counts the filters taken over, the warnings name the others.
    /// </summary>
    public static ExportText Build(TablePresentation presentation, IEnumerable<FilterCondition> filters, string? source = null)
    {
        if (Unavailable(presentation) is { } reason)
        {
            throw new InvalidOperationException(reason);
        }

        var conditions = new List<string>();
        var skipped = new List<string>();
        foreach (var filter in filters.Where(f => f.Enabled))
        {
            var result = Condition(presentation, filter);
            if (result.Code is { } code)
            {
                conditions.Add(code);
            }
            else
            {
                skipped.Add($"{Describe(presentation, filter)}: {result.Problem}");
            }
        }

        var text = new System.Text.StringBuilder();
        foreach (var line in skipped)
        {
            text.Append("// ").Append(TextFormat.Format(ClrModelText.FilterSkippedComment, line)).Append("\r\n");
        }

        var indent = source is null ? "" : "    ";
        if (source is not null)
        {
            text.Append(source);
        }

        if (conditions.Count > 0)
        {
            if (source is not null)
            {
                text.Append("\r\n").Append(indent);
            }

            var head = $".Where({Parameter} => ";
            var continuation = "\r\n" + indent + new string(' ', head.Length - 3) + "&& ";
            text.Append(head).AppendJoin(continuation, conditions).Append(')');
        }

        return new ExportText(text.ToString().TrimEnd(), conditions.Count,
            skipped.Select(s => TextFormat.Format(ClrModelText.FilterSkipped, s)).ToList());
    }

    /// <summary>A filter as the user sees it: <c>KUNDENART = 7</c>.</summary>
    private static string Describe(TablePresentation presentation, FilterCondition filter) =>
        $"{filter.Column} {OperatorLabels.Label(filter.Op)} {string.Join("; ", filter.Values)}".TrimEnd();

    private static CSharpValue Condition(TablePresentation presentation, FilterCondition filter)
    {
        var index = presentation.Details.IndexOf(filter.Column);
        if (index < 0)
        {
            return CSharpValue.Fails(ClrModelText.ColumnGone);
        }

        if (presentation.PropertyOf(index) is not { } property)
        {
            return CSharpValue.Fails(ClrModelText.NoProperty);
        }

        if (!presentation.IsEntityProperty(index))
        {
            return CSharpValue.Fails(TextFormat.Format(ClrModelText.PropertyOfOtherEntity, property.Name));
        }

        var column = presentation.Details.Columns[index];
        var values = presentation.ValuesOf(index);
        var member = property.IsShadow
            ? $"EF.Property<{TablePresentation.ClrTypeText(property)}>({Parameter}, {CSharpCode.String(property.Name)})"
            : $"{Parameter}.{CSharpCode.Identifier(property.Name)}";
        var builder = new ConditionBuilder(property, values, column, member);
        return builder.Build(filter);
    }

    private sealed class ConditionBuilder(PropertyExport property, ValueTable? values, ColumnInfo column, string m)
    {
        private readonly ColumnCategory _category = ColumnCategories.Of(column);

        private bool IsString => property.ClrType == "string" && values is null && property.Converter is null;

        private bool IsBool => property.ClrType == "bool";

        /// <summary>A date column compared as DateTime/DateTimeOffset: a date without time means the whole day.</summary>
        private bool ByDay => _category is ColumnCategory.Date or ColumnCategory.Timestamp or ColumnCategory.TimestampWithTimeZone
                              && property.ClrType is "DateTime" or "DateTimeOffset";

        public CSharpValue Build(FilterCondition filter)
        {
            try
            {
                return filter.Op switch
                {
                    FilterOperator.IsNull => new CSharpValue($"{m} == null"),
                    FilterOperator.IsNotNull => new CSharpValue($"{m} != null"),
                    FilterOperator.Contains => Like(filter, "Contains"),
                    FilterOperator.StartsWith => Like(filter, "StartsWith"),
                    FilterOperator.EndsWith => Like(filter, "EndsWith"),
                    FilterOperator.In => In(filter.Values),
                    _ when IsBool => BoolCondition(filter),
                    _ when ByDay => DayCondition(filter),
                    FilterOperator.Between => new CSharpValue($"{Compare(">=", filter.Values[0])} && {Compare("<=", filter.Values[1])}"),
                    _ => new CSharpValue(Compare(Symbol(filter.Op), filter.Values[0])),
                };
            }
            catch (NoLiteralException e)
            {
                return CSharpValue.Fails(e.Message);
            }
        }

        /// <summary>Case-insensitive like the grid: <c>x.Name.ToUpper().Contains("MEIER")</c>.</summary>
        private CSharpValue Like(FilterCondition filter, string method) =>
            IsString
                ? new CSharpValue($"{m}.ToUpper().{method}({CSharpCode.String(filter.Values[0].ToUpperInvariant())})")
                : CSharpValue.Fails(TextFormat.Format(ClrModelText.OperatorStringOnly, OperatorLabels.Label(filter.Op), TablePresentation.ClrTypeText(property)));

        private CSharpValue In(IReadOnlyList<string> texts)
        {
            if (ByDay && texts.Any(t => Date(t).DateOnly))
            {
                var parts = texts.Select(t => Date(t) is { DateOnly: true } day
                    ? $"({m} >= {DateLiteral(day.Value)} && {m} < {DateLiteral(day.Value.AddDays(1))})"
                    : $"{m} == {Literal(t)}").ToList();
                return new CSharpValue(parts.Count == 1 ? parts[0] : $"({string.Join(" || ", parts)})");
            }

            return new CSharpValue(
                $"new {TablePresentation.ClrTypeText(property)}[] {{ {string.Join(", ", texts.Select(Literal))} }}.Contains({m})");
        }

        /// <summary><c>x.Gesperrt</c>, <c>!x.Gesperrt</c>; nullable bools compare with == / != (NULL counts as "not equal").</summary>
        private CSharpValue BoolCondition(FilterCondition filter)
        {
            if (filter.Op is not (FilterOperator.Equals or FilterOperator.NotEquals))
            {
                return CSharpValue.Fails(TextFormat.Format(ClrModelText.OperatorNotForBool, OperatorLabels.Label(filter.Op)));
            }

            var value = Literal(filter.Values[0]);
            if (property.Nullable)
            {
                return new CSharpValue($"{m} {Symbol(filter.Op)} {value}");
            }

            var positive = value == "true" == (filter.Op == FilterOperator.Equals);
            return new CSharpValue(positive ? m : $"!{m}");
        }

        /// <summary>The same ranges as <see cref="QueryBuilder"/>: "= 1.10." covers 00:00 to before 2.10.</summary>
        private CSharpValue DayCondition(FilterCondition filter)
        {
            var first = Date(filter.Values[0]);
            if (!first.DateOnly && filter.Op != FilterOperator.Between)
            {
                return new CSharpValue(Compare(Symbol(filter.Op), filter.Values[0]));
            }

            var day = DateLiteral(first.Value);
            var next = DateLiteral(first.Value.AddDays(1));
            return new CSharpValue(filter.Op switch
            {
                FilterOperator.Equals => $"{m} >= {day} && {m} < {next}",
                FilterOperator.NotEquals => $"({m} < {day} || {m} >= {next}{(property.Nullable ? $" || {m} == null" : "")})",
                FilterOperator.Gt => $"{m} >= {next}",
                FilterOperator.Gte => $"{m} >= {day}",
                FilterOperator.Lt => $"{m} < {day}",
                FilterOperator.Lte => $"{m} < {next}",
                FilterOperator.Between => Date(filter.Values[1]) is { DateOnly: true } to
                    ? $"{m} >= {Literal(filter.Values[0])} && {m} < {DateLiteral(to.Value.AddDays(1))}"
                    : $"{m} >= {Literal(filter.Values[0])} && {m} <= {Literal(filter.Values[1])}",
                _ => throw new NoLiteralException(TextFormat.Format(ClrModelText.OperatorNotForDates, OperatorLabels.Label(filter.Op))),
            });
        }

        /// <summary><c>x.Wert &gt; 5</c>; strings through <c>string.Compare</c> (EF translates it to the plain comparison).</summary>
        private string Compare(string symbol, string text) =>
            IsString && symbol is not ("==" or "!=")
                ? $"string.Compare({m}, {Literal(text)}) {symbol} 0"
                : $"{m} {symbol} {Literal(text)}";

        private string DateLiteral(DateTime day) => Literal(day);

        private string Literal(string text) => Literal(Raw(text));

        private string Literal(object raw) =>
            CSharpCode.Value(property, values, column, raw) is { Code: { } code } ? code
            : throw new NoLiteralException(CSharpCode.Value(property, values, column, raw).Problem!);

        /// <summary>The filter text as the grid would read it from the column: decimal, DateTime, bytes or text.</summary>
        private object Raw(string text) => _category switch
        {
            ColumnCategory.Number when FilterRules.TryParseNumber(text, out var number) => number,
            ColumnCategory.Date or ColumnCategory.Timestamp or ColumnCategory.TimestampWithTimeZone => Date(text).Value,
            ColumnCategory.Raw when FilterRules.TryParseHex(text, out var bytes) => bytes,
            _ => text,
        };

        private static (DateTime Value, bool DateOnly) Date(string text) =>
            FilterRules.TryParseDate(text, out var value, out var dateOnly)
                ? (value, dateOnly)
                : throw new NoLiteralException(TextFormat.Format(ClrModelText.NotADate, text));

        private static string Symbol(FilterOperator op) => op switch
        {
            FilterOperator.Equals => "==",
            FilterOperator.NotEquals => "!=",
            FilterOperator.Gt => ">",
            FilterOperator.Gte => ">=",
            FilterOperator.Lt => "<",
            FilterOperator.Lte => "<=",
            _ => throw new NoLiteralException(TextFormat.Format(ClrModelText.OperatorNotTranslated, OperatorLabels.Label(op))),
        };
    }

    private sealed class NoLiteralException(string message) : Exception(message);
}
