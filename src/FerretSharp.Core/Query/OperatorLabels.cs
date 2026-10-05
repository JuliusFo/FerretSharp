namespace FerretSharp.Core.Query;

public static class OperatorLabels
{
    public static string Label(FilterOperator op) => op switch
    {
        FilterOperator.Equals => "=",
        FilterOperator.NotEquals => "≠",
        FilterOperator.Contains => "enthält",
        FilterOperator.StartsWith => "beginnt mit",
        FilterOperator.EndsWith => "endet mit",
        FilterOperator.Gt => ">",
        FilterOperator.Gte => "≥",
        FilterOperator.Lt => "<",
        FilterOperator.Lte => "≤",
        FilterOperator.Between => "zwischen",
        FilterOperator.In => "in Liste",
        FilterOperator.IsNull => "ist NULL",
        FilterOperator.IsNotNull => "ist nicht NULL",
        _ => op.ToString(),
    };
}
