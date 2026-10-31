using FerretSharp.Core.Schema;
using static FerretSharp.Core.Oracle.OracleReading;

namespace FerretSharp.Core.Oracle;

/// <summary>Synonyms on an object, the private synonyms of a schema and the names a new one cannot take (WP-68).</summary>
public sealed partial class OracleSchemaReader
{
    // Private ones first, then the public ones; a synonym over a database link points at another database's object. Filtered
    // by the target, not by OWNER (the rule of the ALL_* views): the private synonyms of every schema are wanted. Oracle has no
    // index on a synonym's target, so this reads all synonyms of the database – tens of thousands of public ones are a few
    // milliseconds, as when the explorer reads the public synonyms on connecting (review of WP-68).
    private const string SynonymsOnSql = """
        SELECT owner, synonym_name
          FROM all_synonyms
         WHERE table_owner = :owner AND table_name = :name AND db_link IS NULL
         ORDER BY CASE owner WHEN 'PUBLIC' THEN 1 ELSE 0 END, owner, synonym_name
        """;

    // The kinds of object a synonym can point at (not a package body, index or trigger of the same name); one row per
    // synonym. A materialized view is a TABLE and a MATERIALIZED VIEW in ALL_OBJECTS – the latter names it (review of WP-68:
    // the type showed TABLE). The synonym's own row in ALL_OBJECTS turns INVALID when its target is dropped – a target that
    // never existed leaves it VALID (WP-68, measured against Oracle 23). For another schema, ALL_SYNONYMS shows only synonyms
    // whose target the session may access.
    private const string SchemaSynonymsSql = """
        SELECT s.synonym_name, s.table_owner, s.table_name, s.db_link,
               NVL(MAX(CASE o.object_type WHEN 'MATERIALIZED VIEW' THEN o.object_type END), MAX(o.object_type)) AS target_type,
               MAX(CASE so.status WHEN 'INVALID' THEN 1 ELSE 0 END) AS invalid,
               MAX(CASE WHEN u.username IS NULL THEN 0 ELSE 1 END) AS owner_exists,
               MAX(CASE WHEN s.table_owner = USER THEN 1 ELSE 0 END) AS own_target
          FROM all_synonyms s
          LEFT JOIN all_objects o
            ON s.db_link IS NULL AND o.owner = s.table_owner AND o.object_name = s.table_name
           AND o.object_type IN ('TABLE', 'VIEW', 'MATERIALIZED VIEW', 'SEQUENCE', 'SYNONYM', 'PACKAGE', 'PROCEDURE', 'FUNCTION',
                                 'TYPE', 'OPERATOR', 'JAVA CLASS')
          LEFT JOIN all_objects so
            ON so.owner = s.owner AND so.object_name = s.synonym_name AND so.object_type = 'SYNONYM'
          LEFT JOIN all_users u
            ON u.username = s.table_owner
         WHERE s.owner = :owner
         GROUP BY s.synonym_name, s.table_owner, s.table_name, s.db_link
         ORDER BY s.synonym_name
        """;

    // Private synonyms share namespace 1 with tables, views, sequences, PL/SQL and types (ORA-00955); public ones are the
    // objects of PUBLIC, all synonyms.
    private const string NameTakenSql = """
        SELECT object_type
          FROM all_objects
         WHERE owner = :owner AND object_name = :name AND namespace = 1 AND ROWNUM = 1
        """;

    public async Task<IReadOnlyList<SynonymInfo>> GetSynonymsAsync(TableRef target, CancellationToken cancellationToken) =>
        await session.ReadListAsync(SynonymsOnSql, [new("owner", target.Owner), new("name", target.Name)],
            reader => new SynonymInfo(reader.GetString(0), reader.GetString(1)), cancellationToken);

    public async Task<IReadOnlyList<SchemaSynonym>> GetSchemaSynonymsAsync(string owner, CancellationToken cancellationToken) =>
        await session.ReadListAsync(SchemaSynonymsSql, [new("owner", owner)], reader =>
        {
            var (targetOwner, dbLink, targetType) = (Text(reader, 1), Text(reader, 3), Text(reader, 4));
            var state = SchemaSynonym.StateOf(
                overDbLink: dbLink is not null,
                targetVisible: targetType is not null,
                synonymInvalid: Int(reader, 5) == 1,
                targetOwnerExists: Int(reader, 6) == 1,
                targetInOwnSchema: Int(reader, 7) == 1);
            return new SchemaSynonym(owner, reader.GetString(0), targetOwner, reader.GetString(2), dbLink, dbLink is null ? targetType : null, state);
        }, cancellationToken, many: true);

    public Task<string?> GetNameTakenAsync(string owner, string name, CancellationToken cancellationToken) =>
        session.ExecuteReaderAsync(NameTakenSql, [new("owner", owner), new("name", name)], async (reader, ct) =>
            await reader.ReadAsync(ct) ? reader.GetString(0) : null, cancellationToken);
}
