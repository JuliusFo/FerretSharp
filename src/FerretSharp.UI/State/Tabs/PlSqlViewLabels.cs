using FerretSharp.Core.Schema;
using FerretSharp.Core.Workspaces;
using FerretSharp.UI.Resources;

namespace FerretSharp.UI.State;

/// <summary>The labels of a PL/SQL tab's views: its view switch and the object menu (WP-70).</summary>
public static class PlSqlViewLabels
{
    /// <summary>A package's source is its specification; the other units have one source.</summary>
    public static string Of(PlSqlView view, PlSqlKind kind) => view switch
    {
        PlSqlView.Source => kind == PlSqlKind.Package ? SchemaViewText.PlSql_ViewSpecification : SchemaViewText.PlSql_ViewSource,
        PlSqlView.Body => SchemaViewText.PlSql_ViewBody,
        PlSqlView.Parameters => SchemaViewText.PlSql_ViewParameters,
        PlSqlView.Errors => SchemaViewText.PlSql_ViewErrors,
        _ => SchemaViewText.PlSql_ViewDependencies,
    };
}
