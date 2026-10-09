using FerretSharp.Core.Resources;

namespace FerretSharp.Core.Query;

public static class OperatorLabels
{
    public static string Label(FilterOperator op) => op switch
    {
        FilterOperator.Equals => "=",
        FilterOperator.NotEquals => "≠",
        FilterOperator.Contains => QueryText.OperatorContains,
        FilterOperator.StartsWith => QueryText.OperatorStartsWith,
        FilterOperator.EndsWith => QueryText.OperatorEndsWith,
        FilterOperator.Gt => ">",
        FilterOperator.Gte => "≥",
        FilterOperator.Lt => "<",
        FilterOperator.Lte => "≤",
        FilterOperator.Between => QueryText.OperatorBetween,
        FilterOperator.In => QueryText.OperatorIn,
        FilterOperator.IsNull => QueryText.OperatorIsNull,
        FilterOperator.IsNotNull => QueryText.OperatorIsNotNull,
        _ => op.ToString(),
    };
}
