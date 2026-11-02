using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Ddl;

/// <summary>
/// A view over a query built in the relationship diagram (WP-69). Always with the owner, like synonyms: the schema of the
/// profile changes what the explorer shows, not the session's schema. Never <c>OR REPLACE</c>: it would silently replace
/// another view of the same name – a name in use is reported before (<see cref="SchemaCache.GetNameTakenAsync"/>).
/// </summary>
public static class ViewDdl
{
    /// <param name="select">The select of the view, values as literals (<see cref="Query.DesignSqlMode.View"/>).</param>
    public static DdlStep Create(TableRef view, string select) =>
        new($"{view.Owner}.{view.Name}", DdlStepKind.CreateView, OracleDdl.CreateView(view, select));
}
