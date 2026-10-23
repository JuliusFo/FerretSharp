using FerretSharp.Core.Connections;
using FerretSharp.Core.Schema;

namespace FerretSharp.UI.State;

/// <summary>
/// Where the columns view's script that adds the picked columns goes: a SQL tab of another connection, opened and
/// connected like the schema comparison's proposal (WP-22) – it runs there through the schema path with its confirmation,
/// never from the menu.
/// </summary>
public static class AddColumnsTargets
{
    /// <summary>The other connections in the order of the list, open or not (opening connects them).</summary>
    public static IReadOnlyList<ConnectionProfile> Of(IEnumerable<ConnectionProfile> profiles, Guid own) =>
        profiles.Where(p => p.Id != own).ToList();

    /// <summary>
    /// The table the script changes on the target: the table there that matches the source as a link finds it (same owner,
    /// same name in the connection's schema, a synonym) – Test and Prod have the same tables, the schema name may differ.
    /// Without a loaded schema (not connected yet) or a match, the source's name in the schema the connection shows.
    /// </summary>
    public static TableRef Table(ConnectionProfile target, SchemaCache? targetSchema, TableRef source)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(source);
        return targetSchema is not null && ShellState.LinkedTable(targetSchema, source) is { } table
            ? table.Ref
            : new TableRef(target.EffectiveSchema, source.Name);
    }
}
