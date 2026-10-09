using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;
using static FerretSharp.Core.Oracle.OracleReading;

namespace FerretSharp.Core.Oracle;

/// <summary>
/// Stored PL/SQL (WP-28, view only): packages, procedures, functions and triggers with their source, parameters,
/// compile errors and dependencies. Everything comes from the ALL_* views, filtered by owner; nothing is run.
/// </summary>
public sealed partial class OracleSchemaReader
{
    // Triggers of dropped tables stay in the recycle bin under BIN$… names.
    private const string PlSqlObjectsSql = """
        SELECT object_name, object_type, status FROM all_objects
         WHERE owner = :owner
           AND object_type IN ('PACKAGE', 'PACKAGE BODY', 'PROCEDURE', 'FUNCTION', 'TRIGGER')
           AND object_name NOT LIKE 'BIN$%'
        """;

    private const string DisabledTriggersSql = "SELECT trigger_name FROM all_triggers WHERE owner = :owner AND status = 'DISABLED'";

    private const string PlSqlInfoSql = """
        SELECT object_type, status, created, last_ddl_time FROM all_objects
         WHERE owner = :owner AND object_name = :name AND object_type IN (:object_type, :body_type)
        """;

    private const string AuthIdSql = """
        SELECT authid FROM all_procedures
         WHERE owner = :owner AND object_name = :name AND object_type = :object_type AND procedure_name IS NULL
        """;

    // WHEN_CLAUSE is a VARCHAR2; the body (TRIGGER_BODY, LONG) is read from ALL_SOURCE like all other source.
    private const string TriggerSql = """
        SELECT trigger_type, triggering_event, table_owner, table_name, base_object_type, when_clause, status
          FROM all_triggers
         WHERE owner = :owner AND trigger_name = :name
        """;

    private const string SourceSql = """
        SELECT text FROM all_source
         WHERE owner = :owner AND name = :name AND type = :object_type
         ORDER BY line
        """;

    // Without owner and name ALL_ARGUMENTS is very slow. DATA_LEVEL > 0 rows (before 18c) describe the components of
    // composite types, not parameters.
    private const string ArgumentsSql = """
        SELECT object_name, overload, subprogram_id, argument_name, position, sequence, in_out, data_type, pls_type,
               type_owner, type_name, type_subname, defaulted
          FROM all_arguments
         WHERE owner = :owner AND data_level = 0
           AND (package_name = :name OR package_name IS NULL AND object_name = :name)
         ORDER BY subprogram_id, overload, sequence
        """;

    // Every subprogram, also those without parameters (Oracle 23 has no ALL_ARGUMENTS row for them). A package has one
    // row of its own (PROCEDURE_NAME null, SUBPROGRAM_ID 0); a standalone unit only that one.
    private const string SubprogramsSql = """
        SELECT NVL(procedure_name, object_name), overload, subprogram_id FROM all_procedures
         WHERE owner = :owner AND object_name = :name AND object_type = :object_type
           AND (procedure_name IS NOT NULL OR object_type <> 'PACKAGE')
        """;

    private const string ErrorsSql = """
        SELECT type, line, position, text, attribute FROM all_errors
         WHERE owner = :owner AND name = :name AND type IN (:object_type, :body_type)
         ORDER BY type, sequence
        """;

    // What the unit uses – for a package, specification and body together, without the body's reference to its own
    // specification. STANDARD/DBMS_STANDARD are referenced by everything and say nothing.
    private const string PlSqlUsesSql = """
        SELECT DISTINCT d.referenced_owner, d.referenced_name, d.referenced_type, o.status
          FROM all_dependencies d
          LEFT JOIN all_objects o
            ON o.owner = d.referenced_owner AND o.object_name = d.referenced_name AND o.object_type = d.referenced_type
         WHERE d.owner = :owner AND d.name = :name AND d.type IN (:object_type, :body_type)
           AND d.referenced_link_name IS NULL
           AND d.referenced_type <> 'NON-EXISTENT'
           AND NOT (d.referenced_owner = 'SYS' AND d.referenced_name IN ('STANDARD', 'DBMS_STANDARD'))
           AND NOT (d.referenced_owner = :owner AND d.referenced_name = :name AND d.referenced_type = :object_type)
         ORDER BY d.referenced_type, d.referenced_owner, d.referenced_name
        """;

    // Others reference the specification only; the package's own body is left out.
    private const string PlSqlUsedBySql = """
        SELECT DISTINCT d.owner, d.name, d.type, o.status
          FROM all_dependencies d
          LEFT JOIN all_objects o ON o.owner = d.owner AND o.object_name = d.name AND o.object_type = d.type
         WHERE d.referenced_owner = :owner AND d.referenced_name = :name AND d.referenced_type = :object_type
           AND NOT (d.owner = :owner AND d.name = :name AND d.type = :body_type)
         ORDER BY d.type, d.owner, d.name
        """;

    // A scan over the schema's source: case-insensitive, limited, cancellable.
    private const string SearchSourceSql = """
        SELECT name, type, line, text FROM all_source
         WHERE owner = :owner
           AND type IN ('PACKAGE', 'PACKAGE BODY', 'PROCEDURE', 'FUNCTION', 'TRIGGER')
           AND name NOT LIKE 'BIN$%'
           AND UPPER(text) LIKE UPPER(:pattern) ESCAPE '\'
         ORDER BY name, type, line
         FETCH FIRST :limit ROWS ONLY
        """;

    public async Task<IReadOnlyList<PlSqlObjectSummary>> GetPlSqlObjectsAsync(string owner, CancellationToken cancellationToken)
    {
        var rows = await session.ReadListAsync(PlSqlObjectsSql, [new("owner", owner)], reader =>
            new PlSqlObjectRow(reader.GetString(0), reader.GetString(1), reader.GetString(2)), cancellationToken, many: true);
        var disabled = (await session.ReadListAsync(DisabledTriggersSql, [new("owner", owner)], r => r.GetString(0), cancellationToken))
            .ToHashSet(StringComparer.Ordinal);
        return PlSqlObjectList.Combine(owner, rows, disabled);
    }

    public async Task<PlSqlObjectInfo> GetPlSqlInfoAsync(PlSqlObjectSummary unit, CancellationToken cancellationToken)
    {
        var parameters = UnitParameters(unit);
        var parts = await session.ReadListAsync(PlSqlInfoSql, parameters, reader =>
            (Type: reader.GetString(0), Status: reader.GetString(1), Created: Date(reader, 2), LastDdl: Date(reader, 3)), cancellationToken);
        var spec = parts.FirstOrDefault(p => p.Type == unit.ObjectType());
        var body = unit.Kind == PlSqlKind.Package ? parts.FirstOrDefault(p => p.Type == unit.ObjectType(PlSqlPart.Body)) : default;

        string? authId = null;
        TriggerInfo? trigger = null;
        if (unit.Kind == PlSqlKind.Trigger)
        {
            trigger = await session.ExecuteReaderAsync(TriggerSql, [new("owner", unit.Owner), new("name", unit.Name)], async (reader, ct) =>
                !await reader.ReadAsync(ct)
                    ? null
                    : new TriggerInfo(
                        Timing: Text(reader, 0)?.Trim() ?? "",
                        Event: Text(reader, 1)?.Trim() ?? "",
                        Table: Text(reader, 2) is { } tableOwner && Text(reader, 3) is { } table ? new TableRef(tableOwner, table) : null,
                        BaseObjectType: Text(reader, 4)?.Trim() ?? "",
                        When: Text(reader, 5)?.Trim() is { Length: > 0 } when ? when : null,
                        Enabled: Text(reader, 6) == "ENABLED"), cancellationToken);
        }
        else
        {
            authId = await session.ExecuteReaderAsync(AuthIdSql,
                [new("owner", unit.Owner), new("name", unit.Name), new("object_type", unit.ObjectType())],
                async (reader, ct) => await reader.ReadAsync(ct) ? Text(reader, 0) : null, cancellationToken);
        }

        return new PlSqlObjectInfo(
            Status: spec.Status ?? "N/A",
            BodyStatus: body.Status,
            Created: spec.Created,
            LastDdl: spec.LastDdl,
            BodyLastDdl: body.LastDdl,
            AuthId: authId,
            Trigger: trigger);
    }

    public async Task<PlSqlSource> GetSourceAsync(PlSqlObjectSummary unit, PlSqlPart part, CancellationToken cancellationToken)
    {
        // A package body of thousands of lines: rows of up to 4000 bytes, fetched in few round trips.
        var lines = await session.ReadListAsync(SourceSql,
            [new("owner", unit.Owner), new("name", unit.Name), new("object_type", unit.ObjectType(part))],
            reader => PlSqlSourceText.LineOf(Text(reader, 0)), cancellationToken, many: true);
        return new PlSqlSource(lines);
    }

    public async Task<IReadOnlyList<PlSqlSubprogram>> GetSubprogramsAsync(PlSqlObjectSummary unit, CancellationToken cancellationToken)
    {
        if (unit.Kind == PlSqlKind.Trigger)
        {
            return [];
        }

        var rows = await session.ReadListAsync(ArgumentsSql, [new("owner", unit.Owner), new("name", unit.Name)], reader =>
            new ArgumentRow(
                Subprogram: reader.GetString(0),
                Overload: Text(reader, 1),
                SubprogramId: Int(reader, 2) ?? 0,
                Name: Text(reader, 3),
                Position: Int(reader, 4) ?? 0,
                Sequence: Int(reader, 5) ?? 0,
                InOut: Text(reader, 6),
                DataType: Text(reader, 7),
                PlsType: Text(reader, 8),
                TypeOwner: Text(reader, 9),
                TypeName: Text(reader, 10),
                TypeSubname: Text(reader, 11),
                Defaulted: Text(reader, 12) == "Y"), cancellationToken, many: true);
        var declared = await session.ReadListAsync(SubprogramsSql,
            [new("owner", unit.Owner), new("name", unit.Name), new("object_type", unit.ObjectType())], reader =>
                new SubprogramRow(reader.GetString(0), Text(reader, 1), Int(reader, 2) ?? 0), cancellationToken);
        return PlSqlArguments.Group(declared, rows, unit.Owner);
    }

    public async Task<IReadOnlyList<PlSqlError>> GetErrorsAsync(PlSqlObjectSummary unit, CancellationToken cancellationToken) =>
        await session.ReadListAsync(ErrorsSql, UnitParameters(unit), reader =>
            new PlSqlError(
                Part: reader.GetString(0) == "PACKAGE BODY" ? PlSqlPart.Body : PlSqlPart.Spec,
                Line: Int(reader, 1) ?? 1,
                Column: Int(reader, 2) ?? 1,
                Text: Text(reader, 3)?.Trim() ?? "",
                IsWarning: Text(reader, 4) == "WARNING"), cancellationToken);

    public async Task<ObjectDependencies> GetDependenciesAsync(PlSqlObjectSummary unit, CancellationToken cancellationToken)
    {
        var parameters = UnitParameters(unit);
        var uses = await ReadDependenciesAsync(PlSqlUsesSql, parameters, cancellationToken);
        var usedBy = await ReadDependenciesAsync(PlSqlUsedBySql, parameters, cancellationToken);
        return new ObjectDependencies(uses, usedBy);
    }

    public async Task<IReadOnlyList<SourceHit>> SearchSourceAsync(string owner, string text, int limit, CancellationToken cancellationToken) =>
        await session.ReadListAsync(SearchSourceSql,
            [new("owner", owner), new("pattern", LikePattern.Contains(text)), new("limit", limit)], reader =>
            {
                var (kind, part) = PlSqlKinds.Of(reader.GetString(1)) ?? (PlSqlKind.Procedure, PlSqlPart.Spec);
                return new SourceHit(new PlSqlRef(owner, reader.GetString(0), kind), part, Int(reader, 2) ?? 1, PlSqlSourceText.LineOf(Text(reader, 3)));
            }, cancellationToken, many: true);

    /// <summary>Owner, name, the unit's object type and that of its body (for other units than packages: the same again).</summary>
    private static QueryParameter[] UnitParameters(PlSqlObjectSummary unit) =>
    [
        new("owner", unit.Owner),
        new("name", unit.Name),
        new("object_type", unit.ObjectType()),
        new("body_type", unit.ObjectType(unit.Kind == PlSqlKind.Package ? PlSqlPart.Body : PlSqlPart.Spec)),
    ];
}
