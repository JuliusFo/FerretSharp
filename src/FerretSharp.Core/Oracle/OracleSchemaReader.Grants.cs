using System.Data.Common;
using FerretSharp.Core.Schema;
using static FerretSharp.Core.Oracle.OracleReading;

namespace FerretSharp.Core.Oracle;

/// <summary>
/// The object privileges on an object and on all objects of a schema, and the users a privilege can go to (WP-71). Measured
/// against Oracle 23: on another schema's objects <c>ALL_TAB_PRIVS</c>/<c>ALL_COL_PRIVS</c> show only grants the session
/// takes part in – as grantor, grantee, through an enabled role or <c>PUBLIC</c>; on the own schema's all of them.
/// </summary>
public sealed partial class OracleSchemaReader
{
    // Privileges on the whole object and per column (ALL_TAB_PRIVS has none per column), one row per column. The rows of type
    // USER are privileges on a user (INHERIT PRIVILEGES), whose TABLE_NAME is the user's name and could match an object's.
    private const string GrantRowsSelect = """
        SELECT table_name, grantee, privilege, NULL AS column_name, grantor, grantable, hierarchy
          FROM all_tab_privs
         WHERE table_schema = :owner AND type <> 'USER'
        UNION ALL
        SELECT table_name, grantee, privilege, column_name, grantor, grantable, 'NO'
          FROM all_col_privs
         WHERE table_schema = :owner
        """;

    private const string GrantsSql = $"""
        SELECT * FROM ({GrantRowsSelect})
         WHERE table_name = :name
         ORDER BY grantee, privilege, grantor, column_name
        """;

    // Every object of the schema a privilege can be granted on – the objects of the explorer, sequences and PL/SQL units –
    // with its grants; an object without any gets one row without grant ("Objekte ohne Recht für X").
    private const string SchemaGrantsSql = $"""
        WITH objs AS (
            {TablesSql}
            UNION ALL
            SELECT sequence_name, 'SEQUENCE' FROM all_sequences WHERE sequence_owner = :owner
            UNION ALL
            SELECT object_name, object_type FROM all_objects
             WHERE owner = :owner AND object_type IN ('PACKAGE', 'PROCEDURE', 'FUNCTION')),
        privs AS ({GrantRowsSelect})
        SELECT o.object_name, o.object_kind, p.grantee, p.privilege, p.column_name, p.grantor, p.grantable, p.hierarchy
          FROM objs o
          LEFT JOIN privs p ON p.table_name = o.object_name
         ORDER BY o.object_name, p.grantee, p.privilege, p.grantor, p.column_name
        """;

    // Roles are not in ALL_USERS, and DBA_ROLES needs more than a developer has.
    private const string UserNamesSql = "SELECT username FROM all_users ORDER BY username";

    public async Task<IReadOnlyList<ObjectGrant>> GetGrantsAsync(TableRef target, CancellationToken cancellationToken)
    {
        var rows = await session.ReadListAsync(GrantsSql, [new("owner", target.Owner), new("name", target.Name)],
            reader => ReadGrantRow(reader, 1), cancellationToken);
        return CombineColumns(rows);
    }

    public async Task<IReadOnlyList<SchemaObjectGrants>> GetSchemaGrantsAsync(string owner, CancellationToken cancellationToken)
    {
        var rows = await session.ReadListAsync(SchemaGrantsSql, [new("owner", owner)],
            reader => (Name: reader.GetString(0), Kind: reader.GetString(1), Grant: reader.IsDBNull(2) ? (GrantRow?)null : ReadGrantRow(reader, 2)),
            cancellationToken, many: true);
        return
        [
            .. rows.GroupBy(r => r.Name, StringComparer.Ordinal).Select(g => new SchemaObjectGrants(
                new TableRef(owner, g.Key), KindOf(g.First().Kind), CombineColumns([.. g.Where(r => r.Grant is not null).Select(r => r.Grant!.Value)]))),
        ];
    }

    public async Task<IReadOnlyList<string>> GetUserNamesAsync(CancellationToken cancellationToken) =>
        await session.ReadListAsync(UserNamesSql, [], reader => reader.GetString(0), cancellationToken, many: true);

    /// <summary>A row of <see cref="GrantRowsSelect"/>: one privilege of a grantee, on the whole object or one column.</summary>
    private readonly record struct GrantRow(string Grantee, string Privilege, string? Column, string Grantor, bool Grantable, bool Hierarchy);

    /// <summary>Grantee, privilege, column, grantor, grantable and hierarchy from <paramref name="first"/> on.</summary>
    private static GrantRow ReadGrantRow(DbDataReader reader, int first) =>
        new(reader.GetString(first), reader.GetString(first + 1), Text(reader, first + 2), reader.GetString(first + 3),
            Text(reader, first + 4) == "YES", Text(reader, first + 5) == "YES");

    /// <summary>The rows of one grant per column become one <see cref="ObjectGrant"/> with its columns; order kept.</summary>
    private static List<ObjectGrant> CombineColumns(IReadOnlyList<GrantRow> rows) =>
    [
        .. rows.GroupBy(r => (r.Grantee, r.Privilege, r.Grantor, r.Grantable, r.Hierarchy, PerColumn: r.Column is not null))
            .Select(g => new ObjectGrant(g.Key.Grantee, g.Key.Privilege, [.. g.Where(r => r.Column is not null).Select(r => r.Column!)],
                g.Key.Grantor, g.Key.Grantable, g.Key.Hierarchy)),
    ];

    private static GrantObjectKind KindOf(string kind) => kind switch
    {
        "VIEW" => GrantObjectKind.View,
        "MVIEW" => GrantObjectKind.MaterializedView,
        "SEQUENCE" => GrantObjectKind.Sequence,
        "PACKAGE" => GrantObjectKind.Package,
        "PROCEDURE" => GrantObjectKind.Procedure,
        "FUNCTION" => GrantObjectKind.Function,
        _ => GrantObjectKind.Table,
    };
}
