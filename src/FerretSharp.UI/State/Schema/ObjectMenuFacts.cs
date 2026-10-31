using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Connections;
using FerretSharp.Core.Data;
using FerretSharp.Core.Oracle;
using FerretSharp.Core.Query;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Schema;
using FerretSharp.UI.Resources;

namespace FerretSharp.UI.State;

/// <summary>
/// What the right-click menu of a table or PL/SQL unit shows and offers to copy (WP-70) – in the explorer and on a tab
/// head – and the kind names the explorer's tooltips share.
/// </summary>
public static class ObjectMenuFacts
{
    public static string KindLabel(TableKind kind) => kind switch
    {
        TableKind.View => SchemaViewText.Explorer_KindView,
        TableKind.MaterializedView => SchemaViewText.Explorer_KindMaterializedView,
        _ => SchemaViewText.Explorer_KindTable,
    };

    /// <summary>The <c>ALL_OBJECTS.OBJECT_TYPE</c> of a table, view or materialized view.</summary>
    public static string ObjectType(TableKind kind) => kind switch
    {
        TableKind.View => "VIEW",
        TableKind.MaterializedView => "MATERIALIZED VIEW",
        _ => "TABLE",
    };

    /// <summary>"Reached through the synonym …" for a tooltip; <paramref name="kind"/> is the real object's kind label.</summary>
    public static string SynonymTooltip(SynonymInfo synonym, string owner, string name, string kind) =>
        TextFormat.Format(synonym.IsPublic ? SchemaViewText.Explorer_PublicSynonymTooltip : SchemaViewText.Explorer_SynonymTooltip, $"{owner}.{name}", kind);

    /// <summary>
    /// The texts to copy, each once, in this order: the name the user knows (the synonym), owner and name of the real
    /// object as code reads them, then – with a C# model – the entity and its LINQ source (<c>db.Kunden</c>).
    /// </summary>
    public static IReadOnlyList<string> CopyTexts(string owner, string name, string displayName, string? entity = null, string? linqSource = null) =>
        new[] { displayName, OracleIdentifier.QualifyIfNeeded(owner, name), entity, linqSource }.OfType<string>().Distinct(StringComparer.Ordinal).ToList();

    /// <summary>
    /// The schema the sessions resolve unqualified names in: the login user's – a schema in the profile changes only what is
    /// shown, no <c>CURRENT_SCHEMA</c> (review of WP-70).
    /// </summary>
    public static string SessionSchema(ConnectionProfile profile) => OracleIdentifier.Normalize(profile.User);

    /// <summary>
    /// "Open in the SQL editor": from a tab with applied filters or a sort order the statement of its filter bar
    /// (<c>SELECT t.*</c>, values as variables) once its structure is loaded – the menu acts on the tab (review of WP-70);
    /// otherwise <c>SELECT *</c> by the name the session finds (<see cref="QueryBuilder.SelectStarText"/>).
    /// </summary>
    public static (string Sql, IReadOnlyList<SqlVariable> Variables) SqlOf(TableSummary table, string sessionSchema, TableTab? tab = null)
    {
        if (tab is { Details: { } details } && (tab.ActiveFilterCount > 0 || tab.Sorts.Count > 0))
        {
            try
            {
                var query = QueryBuilder.BuildSelectStar(details, tab.AppliedFilters, tab.Sorts);
                return (query.Sql + ";\n", query.Parameters.Select(SqlBinds.FromParameter).ToList());
            }
            catch (QueryValidationException)
            {
                // A filter of a column that is gone (the structure changed since): the plain statement still helps.
            }
        }

        return (QueryBuilder.SelectStarText(table, sessionSchema) + ";\n", []);
    }

    /// <summary>
    /// "Open in the LINQ console": the entity's DbSet, from a tab with applied filters its <c>Where</c> as in the filter bar
    /// (warnings name what could not be taken over); null without an entity.
    /// </summary>
    public static ExportText? LinqOf(PresentationService presentations, TableRef table, TableTab? tab = null)
    {
        if (tab is { Details: { } details } && tab.ActiveFilterCount > 0 && presentations.For(details) is { Entity: not null } presentation)
        {
            return LinqFilter.Build(presentation, tab.AppliedFilters, LinqFilter.Source(presentation));
        }

        return presentations.LinqSourceOf(table) is { } source ? new ExportText(source, 0, []) : null;
    }
}
