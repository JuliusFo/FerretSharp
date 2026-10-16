using FerretSharp.Core.Resources;

namespace FerretSharp.Core.Query;

/// <summary>Names of the kinds of filter groups (WP-50): "ODER", "NICHT".</summary>
public static class FilterGroupLabels
{
    public static string Label(FilterGroupKind kind) => kind switch
    {
        FilterGroupKind.Any => QueryText.FilterGroupAny,
        FilterGroupKind.None => QueryText.FilterGroupNone,
        _ => kind.ToString(),
    };
}
