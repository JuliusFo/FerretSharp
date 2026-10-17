using FerretSharp.UI.Resources;

namespace FerretSharp.UI.State;

/// <summary>What <c>STATUS = 'INVALID'</c> means for the user, by object type (tooltip of the red markers).</summary>
public static class InvalidStatusText
{
    /// <param name="objectType"><c>ALL_OBJECTS.OBJECT_TYPE</c>, e.g. VIEW, MATERIALIZED VIEW, PACKAGE BODY.</param>
    public static string For(string objectType) => objectType switch
    {
        "MATERIALIZED VIEW" => SchemaViewText.Invalid_MaterializedView,
        "VIEW" => SchemaViewText.Invalid_View,
        _ => SchemaViewText.Invalid_Other,
    };
}
