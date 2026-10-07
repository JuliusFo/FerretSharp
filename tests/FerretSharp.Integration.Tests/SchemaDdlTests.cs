using FerretSharp.Core.Compare;
using FerretSharp.Core.Oracle;
using FerretSharp.Core.Schema;
using Oracle.ManagedDataAccess.Client;
using static FerretSharp.Core.Tests.Compare.TestComparison;

namespace FerretSharp.Integration.Tests;

/// <summary>
/// The DDL proposal (WP-20) is valid Oracle: a target schema in the container is aligned with a reference built by
/// hand, the proposal's statements run with a plain connection, and a new comparison then proposes nothing executable.
/// </summary>
public sealed class SchemaDdlTests(OracleContainerFixture oracle) : IAsyncLifetime
{
    private const string ReferenceOwner = "REF";

    private static readonly string[] TargetSetup =
    [
        """
        CREATE TABLE DA_KUNDE (
            ID      NUMBER(10) CONSTRAINT PK_DA_KUNDE PRIMARY KEY,
            NAME    VARCHAR2(30 BYTE),
            LAND    VARCHAR2(2 BYTE) DEFAULT ON NULL 'AT',
            ALT     VARCHAR2(10 BYTE),
            MENGE   NUMBER(5,2) CONSTRAINT CK_DA_MENGE CHECK (MENGE >= 1),
            CODE    VARCHAR2(10 BYTE) NOT NULL,
            FLAG    VARCHAR2(1 BYTE) DEFAULT ON NULL 'J',
            "Notiz" VARCHAR2(100 BYTE))
        """,
        "CREATE INDEX IX_DA_KUNDE_NAME ON DA_KUNDE (NAME)",
        "CREATE INDEX IX_DA_KUNDE_ALT ON DA_KUNDE (ALT)",
        "INSERT INTO DA_KUNDE (ID, NAME, LAND, MENGE, CODE) VALUES (1, 'Muster', 'DE', 2, 'A')",
        "CREATE TABLE DA_ALT (ID NUMBER)",
    ];

    private OracleSession? _session;
    private string _owner = "";
    private string _connectionString = "";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private OracleSchemaReader Reader => new(_session!);

    public async ValueTask InitializeAsync()
    {
        var (profile, password) = oracle.RequireProfile();
        _connectionString = oracle.RequireConnectionString();
        _owner = profile.EffectiveSchema;
        await ExecuteAsync(TargetSetup);
        _session = await OracleSession.OpenAsync(
            OracleConnectionStringFactory.Create(profile, password), new SessionContext("FerretSharp", "DDL tests"), Ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_session is not null)
        {
            await _session.DisposeAsync();
        }
    }

    [Fact]
    public async Task Proposed_statements_run_and_align_the_target_with_the_reference()
    {
        var reference = Snapshot(ReferenceOwner, ReferenceObjects());
        var proposal = SchemaDdl.Align(Of(reference, await ReadTargetAsync()), 0, 1);

        var statements = proposal.Steps.Where(s => !s.IsCommented).Select(s => s.Sql).ToList();
        Assert.True(statements.Count > 20, proposal.Script);
        await ExecuteAsync(statements);

        // Run again: what is left are the hints and drops the user decides about.
        var again = SchemaDdl.Align(Of(reference, await ReadTargetAsync()), 0, 1);
        Assert.True(again.Steps.All(s => s.IsCommented), again.Script);
        Assert.Contains(again.Steps, s => s.Sql == $"-- ALTER TABLE \"{_owner}\".\"DA_KUNDE\" DROP COLUMN \"ALT\"");
        Assert.Contains(again.Steps, s => s.Sql == $"-- DROP TABLE \"{_owner}\".\"DA_ALT\"");
        Assert.Contains(again.Steps, s => s.Sql == $"-- DROP INDEX \"{_owner}\".\"IX_DA_KUNDE_ALT\"");
        Assert.Contains(again.Steps, s => s.Sql.StartsWith($"-- CREATE VIEW \"{_owner}\".\"DA_V\"", StringComparison.Ordinal));

        // A few things the comparison helper does not look at.
        var kunde = await Reader.GetDetailsAsync(new TableSummary(_owner, "DA_KUNDE", TableKind.Table), Ct);
        Assert.Equal("Anzeigename", kunde.Columns.Single(c => c.Name == "NAME").Comment);
        Assert.Contains(kunde.Columns, c => c.Name == "ALT"); // drops stay comments
        var land = kunde.Columns.Single(c => c.Name == "LAND");
        Assert.False(land.DefaultOnNull);
        Assert.True(land.Nullable); // Oracle 23 took NOT NULL away with ON NULL; the NULL step stayed a hint
        Assert.Contains(proposal.Steps, s => s.Sql == $"-- ALTER TABLE \"{_owner}\".\"DA_KUNDE\" MODIFY (\"LAND\" NULL)");
        var flag = kunde.Columns.Single(c => c.Name == "FLAG");
        Assert.False(flag.DefaultOnNull);
        Assert.False(flag.Nullable);

        var pos = await Reader.GetDetailsAsync(new TableSummary(_owner, "DA_POS", TableKind.Table), Ct);
        Assert.True(pos.IsIndexOrganized);
        Assert.True((await Reader.GetObjectInfoAsync(new TableSummary(_owner, "DA_TEMP", TableKind.Table), Ct)).Temporary);

        var auftrag = await Reader.GetIndexesAsync(new TableRef(_owner, "DA_AUFTRAG"), Ct);
        Assert.Contains(auftrag, i => i.Name == "IX_DA_AUFTRAG_DATUM"); // the generated name got a readable one
    }

    private static ObjectSnapshot[] ReferenceObjects()
    {
        var kunde = Table("DA_KUNDE",
            [
                Column("ID", "NUMBER", precision: 10, scale: 0, nullable: false),
                Column("NAME", "VARCHAR2", length: 50, charSemantics: true, nullable: false, comment: "Anzeigename"),
                Column("LAND", "VARCHAR2", length: 2, @default: "'DE'"),
                Column("MENGE", "NUMBER", precision: 7, scale: 2),
                Column("CODE", "VARCHAR2", length: 10, @default: "'X'"),
                Column("FLAG", "VARCHAR2", length: 1, @default: "'J'", nullable: false),
                Column("Notiz", "VARCHAR2", length: 100),
                Column("EMAIL", "VARCHAR2", length: 200, charSemantics: true),
                Column("STATUS", "CHAR", length: 1, nullable: false, @default: "'A'"),
                Column("KENNZ", "VARCHAR2", length: 1, @default: "'J'", defaultOnNull: true),
            ],
            [
                PrimaryKey("PK_DA_KUNDE", "ID"),
                Unique("UQ_DA_KUNDE_EMAIL", "EMAIL"),
                Check("CK_DA_MENGE", "MENGE >= 0"),
                Check("SYS_C9000", "STATUS IN ('A', 'I')"),
            ],
            [
                UniqueIndex(ReferenceOwner, "PK_DA_KUNDE", On("ID")),
                UniqueIndex(ReferenceOwner, "UQ_DA_KUNDE_EMAIL", On("EMAIL")),
                Index(ReferenceOwner, "IX_DA_KUNDE_NAME", Expression("UPPER(\"NAME\")")),
                Index(ReferenceOwner, "IX_DA_KUNDE_LAND", On("LAND", descending: true), On("MENGE")) with { IndexType = "FUNCTION-BASED NORMAL" },
            ]);

        var auftrag = Table("DA_AUFTRAG",
            [
                Column("ID", "NUMBER", identity: true),
                Column("KUNDE_ID", "NUMBER", precision: 10, scale: 0, nullable: false),
                Column("DATUM", "DATE", nullable: false, @default: "SYSDATE"),
                Column("BETRAG", "NUMBER", precision: 12, scale: 2),
                Column("BRUTTO", "NUMBER", isVirtual: true, @default: "\"BETRAG\"*1.19"),
                Column("ZEIT", "TIMESTAMP(3)", scale: 3),
                Column("HASH", "RAW", length: 16),
                Column("NOTIZ", "CLOB"),
                Column("Bemerkung", "NVARCHAR2", length: 50),
                Column("MENGE", "NUMBER", scale: 0),
            ],
            [
                PrimaryKey("SYS_C9001", "ID"),
                ForeignKey("FK_DA_AUFTRAG_KUNDE", ["KUNDE_ID"], new TableRef(ReferenceOwner, "DA_KUNDE"), ["ID"], "CASCADE"),
                Check("SYS_C9002", "BETRAG >= 0"),
            ],
            [
                UniqueIndex(ReferenceOwner, "SYS_C9001", On("ID")),
                Index(ReferenceOwner, "IX_DA_AUFTRAG_KUNDE", On("KUNDE_ID")),
                Index(ReferenceOwner, "SYS_C9003", On("DATUM")),
            ]);

        var pos = Table("DA_POS",
            [
                Column("AUFTRAG_ID", "NUMBER", nullable: false),
                Column("POS", "NUMBER", precision: 3, scale: 0, nullable: false),
                Column("TEXT", "VARCHAR2", length: 100, charSemantics: true),
            ],
            [
                PrimaryKey("PK_DA_POS", "AUFTRAG_ID", "POS"),
                ForeignKey("SYS_C9004", ["AUFTRAG_ID"], new TableRef(ReferenceOwner, "DA_AUFTRAG"), ["ID"], deferred: true),
            ],
            [UniqueIndex(ReferenceOwner, "PK_DA_POS", On("AUFTRAG_ID"), On("POS")) with { IndexType = "IOT - TOP" }],
            iot: true);

        var notiz = Table("DaNotiz",
            [Column("Id", "NUMBER", nullable: false), Column("Text", "VARCHAR2", length: 20), Column("Code", "VARCHAR2", length: 10)],
            [PrimaryKey("Pk_DaNotiz", "Id"), Unique("Uq_DaNotiz_Code", "Code") with { Deferrable = true }],
            [UniqueIndex(ReferenceOwner, "DaNotiz_Id_Ux", On("Id")), Index(ReferenceOwner, "DaNotiz_Code_Ix", On("Code"))]);

        var temp = Table("DA_TEMP", [Column("ID", "NUMBER")], temporary: true);
        var view = Table("DA_V", [Column("ID", "NUMBER")], kind: TableKind.View);

        return [kunde, auftrag, pos, notiz, temp, view];
    }

    /// <summary>The test's tables in the container's schema as a snapshot, read with the per-table reader methods.</summary>
    private async Task<SchemaSnapshot> ReadTargetAsync()
    {
        var objects = new List<ObjectSnapshot>();
        var tables = await Reader.GetTablesAsync(_owner, Ct);
        foreach (var table in tables.Where(t => t.Name.StartsWith("DA_", StringComparison.Ordinal) || t.Name == "DaNotiz"))
        {
            var details = await Reader.GetDetailsAsync(table, Ct);
            var info = await Reader.GetObjectInfoAsync(table, Ct);
            var constraints = await Reader.GetConstraintsAsync(table.Ref, Ct);
            var indexes = await Reader.GetIndexesAsync(table.Ref, Ct);
            objects.Add(new ObjectSnapshot(
                table.Name,
                table.Kind,
                details.Columns,
                constraints.Where(c => c.Type is ConstraintType.PrimaryKey or ConstraintType.Unique or ConstraintType.ForeignKey or ConstraintType.Check && !c.IsColumnNotNull).ToList(),
                indexes,
                details.IsIndexOrganized,
                info.Temporary,
                info.Partitioned));
        }

        return Snapshot(_owner, [.. objects]);
    }

    private async Task ExecuteAsync(IEnumerable<string> statements)
    {
        await using var connection = new OracleConnection(_connectionString);
        await connection.OpenAsync(Ct);
        foreach (var sql in statements)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            try
            {
                await command.ExecuteNonQueryAsync(Ct);
            }
            catch (OracleException ex)
            {
                throw new InvalidOperationException($"{ex.Message}\n{sql}", ex);
            }
        }
    }
}
