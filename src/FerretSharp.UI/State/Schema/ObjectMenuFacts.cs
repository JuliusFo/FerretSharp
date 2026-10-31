using FerretSharp.Core.Oracle;
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
}
