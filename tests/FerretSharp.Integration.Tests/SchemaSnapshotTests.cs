using System.Text.Json;
using FerretSharp.Core.Compare;
using FerretSharp.Core.Oracle;
using FerretSharp.Core.Schema;

namespace FerretSharp.Integration.Tests;

/// <summary>The structure of a whole schema in a few queries (WP-20), checked against the per-table methods.</summary>
public sealed class SchemaSnapshotTests(OracleContainerFixture oracle) : IAsyncLifetime
{
    private const string Owner = SnapshotSchema.Owner;

    private OracleSession? _session;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private OracleSchemaReader Reader => new(_session!);

    public async ValueTask InitializeAsync()
    {
        var (profile, _) = oracle.RequireProfile();
        await SnapshotSchema.EnsureCreatedAsync(oracle.RequireConnectionString(), Ct);
        Assert.SkipUnless(SnapshotSchema.Available, SnapshotSchema.UnavailableReason ?? "Snapshot schema not created.");

        _session = await OracleSession.OpenAsync(
            OracleConnectionStringFactory.Create(profile with { User = Owner }, SnapshotSchema.Password),
            new SessionContext("FerretSharp", "Snapshot tests"),
            Ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_session is not null)
        {
            await _session.DisposeAsync();
        }
    }

    [Fact]
    public async Task Snapshot_equals_what_the_per_table_methods_read()
    {
        var snapshot = await Reader.ReadSnapshotAsync(Owner, null, Ct);
        var tables = await Reader.GetTablesAsync(Owner, Ct);

        Assert.Equal(Owner, snapshot.Owner);
        Assert.Equal(tables.Select(t => (t.Name, t.Kind)), snapshot.Objects.Select(o => (o.Name, o.Kind)));
        foreach (var (table, snap) in tables.Zip(snapshot.Objects))
        {
            var details = await Reader.GetDetailsAsync(table, Ct);
            var info = await Reader.GetObjectInfoAsync(table, Ct);
            var isView = table.Kind == TableKind.View;
            var constraints = isView ? [] : (await Reader.GetConstraintsAsync(table.Ref, Ct)).Where(c => !c.IsColumnNotNull).ToList();
            var indexes = isView ? [] : await Reader.GetIndexesAsync(table.Ref, Ct);

            AssertSame(details.Columns, snap.Columns, table.Name);
            AssertSame(constraints, snap.Constraints, table.Name);
            AssertSame(indexes, snap.Indexes, table.Name);
            Assert.Equal(
                (details.IsIndexOrganized, info.Temporary, info.Partitioned),
                (snap.IsIndexOrganized, snap.Temporary, snap.Partitioned));
        }
    }

    [Fact]
    public async Task Snapshot_reads_varied_objects()
    {
        var snapshot = await Reader.ReadSnapshotAsync(Owner, null, Ct);
        var objects = snapshot.Objects.ToDictionary(o => o.Name);

        Assert.Equal(snapshot.Objects.Select(o => o.Name).Order(StringComparer.Ordinal), snapshot.Objects.Select(o => o.Name));
        Assert.DoesNotContain(objects.Keys, n => n.StartsWith("BIN$", StringComparison.Ordinal) || n.StartsWith("SYS_IOT_OVER", StringComparison.Ordinal));

        // The trap behind filtering by the object list: a dropped table leaves ALL_TABLES, but its constraints stay in
        // ALL_CONSTRAINTS under the BIN$… name (Oracle 23).
        var binConstraints = await _session!.ExecuteReaderAsync(
            "SELECT COUNT(*) FROM all_constraints WHERE owner = :owner AND table_name LIKE 'BIN$%'",
            [new("owner", Owner)],
            async (reader, ct) => await reader.ReadAsync(ct) ? Convert.ToInt32(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture) : 0,
            Ct);
        Assert.True(binConstraints > 0, "The recycle bin should hold the constraints of the dropped table.");

        // quoted mixed-case names, comment, virtual column, DEFAULT ON NULL
        var kunde = objects["Kunde"];
        Assert.Equal(["Id", "NAME", "eMail", "LAND", "STATUS", "RABATT", "NAME_GROSS"], kunde.Columns.Select(c => c.Name));
        var kundeColumns = kunde.Columns.ToDictionary(c => c.Name);
        Assert.Equal("Adresse für Rechnungen", kundeColumns["eMail"].Comment);
        Assert.True(kundeColumns["NAME_GROSS"].IsVirtual);
        Assert.Contains("UPPER", kundeColumns["NAME_GROSS"].Default);
        Assert.True(kundeColumns["LAND"].DefaultOnNull);
        Assert.Equal("'A'", kundeColumns["STATUS"].Default);
        Assert.False(kundeColumns["NAME"].Nullable);

        // the NOT NULL checks (NAME, LAND through DEFAULT ON NULL) are left out; system-named unique and check stay
        Assert.DoesNotContain(kunde.Constraints, c => c.IsColumnNotNull);
        Assert.Equal(
            [ConstraintType.PrimaryKey, ConstraintType.Unique, ConstraintType.Check, ConstraintType.Check],
            kunde.Constraints.Select(c => c.Type));
        Assert.Equal("PK_Kunde", kunde.Constraints[0].Name);
        var unique = kunde.Constraints[1];
        Assert.True(unique.GeneratedName);
        Assert.Equal(["eMail"], unique.Columns);
        var status = Assert.Single(kunde.Constraints, c => c.Type == ConstraintType.Check && c.GeneratedName);
        Assert.Contains("STATUS IN", status.Condition);
        var rabatt = Assert.Single(kunde.Constraints, c => c.Name == "CK_KUNDE_RABATT");
        Assert.False(rabatt.Enabled);
        var upper = Assert.Single(Assert.Single(kunde.Indexes, i => i.Name == "ix_Kunde_Name").Columns);
        Assert.True(upper.IsExpression);

        // identity, composite primary key, foreign keys within and across schemas, deferrable unique
        var auftrag = objects["AUFTRAG"];
        Assert.True(auftrag.Columns.Single(c => c.Name == "AUFTRAG_ID").IsIdentity);
        var constraints = auftrag.Constraints.ToDictionary(c => c.Name);
        Assert.Equal(["AUFTRAG_ID", "POS_NR"], constraints["PK_AUFTRAG"].Columns);
        var toKunde = constraints["FK_AUFTRAG_KUNDE"];
        Assert.Equal((new TableRef(Owner, "Kunde"), "CASCADE"), (toKunde.References, toKunde.DeleteRule));
        Assert.Equal(["Id"], toKunde.ReferencedColumns);
        var toKategorie = constraints["FK_AUFTRAG_KATEGORIE"];
        Assert.Equal((new TableRef(SnapshotSchema.RefOwner, "KATEGORIE"), "SET NULL"), (toKategorie.References, toKategorie.DeleteRule));
        Assert.Equal(["ID"], toKategorie.ReferencedColumns);
        Assert.True(constraints["UQ_AUFTRAG_KUNDE_POS"].Deferrable);
        Assert.True(constraints["UQ_AUFTRAG_KUNDE_POS"].InitiallyDeferred);
        Assert.Equal(["KUNDE_ID", "POS_NR"], constraints["UQ_AUFTRAG_KUNDE_POS"].Columns);
        Assert.Contains("MENGE > 0", constraints["CK_AUFTRAG_MENGE"].Condition);

        // descending and function-based index columns; no LOB index
        var indexes = auftrag.Indexes.ToDictionary(i => i.Name);
        Assert.Equal([new IndexColumn("KUNDE_ID", false, true), new IndexColumn("AUFTRAG_ID", false, false)], indexes["IX_AUFTRAG_KUNDE"].Columns);
        var menge = indexes["IX_AUFTRAG_MENGE"].Columns;
        Assert.True(menge[0].IsExpression);
        Assert.Contains("ROUND", menge[0].Name);
        Assert.Equal(new IndexColumn("POS_NR", false, false), menge[1]);
        Assert.DoesNotContain(auftrag.Indexes, i => i.IndexType == "LOB");

        Assert.True(objects["LAND_IOT"].IsIndexOrganized);
        Assert.False(auftrag.IsIndexOrganized);
        Assert.True(objects["TMP_IMPORT"].Temporary);
        Assert.False(auftrag.Temporary);
        if (SnapshotSchema.HasPartitionedTable)
        {
            Assert.True(objects["PROTOKOLL"].Partitioned);
        }

        // a view has columns but neither constraints (WITH READ ONLY) nor indexes; the mview is there once
        var view = objects["V_KUNDE"];
        Assert.Equal(TableKind.View, view.Kind);
        Assert.Equal(["Id", "NAME"], view.Columns.Select(c => c.Name));
        Assert.Empty(view.Constraints);
        Assert.Empty(view.Indexes);
        Assert.Contains(await Reader.GetConstraintsAsync(new TableRef(Owner, "V_KUNDE"), Ct), c => c.Type == ConstraintType.ViewReadOnly);
        Assert.Equal(TableKind.MaterializedView, objects["MV_MENGE"].Kind);
        Assert.Equal(["KUNDE_ID", "MENGE"], objects["MV_MENGE"].Columns.Select(c => c.Name));
    }


    [Fact]
    public async Task Empty_schema_gives_an_empty_snapshot()
    {
        var snapshot = await Reader.ReadSnapshotAsync(SnapshotSchema.EmptyOwner, null, Ct);

        Assert.Equal(SnapshotSchema.EmptyOwner, snapshot.Owner);
        Assert.Empty(snapshot.Objects);
    }

    [Fact]
    public async Task Reports_its_steps()
    {
        var progress = new Steps();
        await Reader.ReadSnapshotAsync(Owner, progress, Ct);

        Assert.Equal(["Objekte", "Spalten", "Constraints", "Indizes"], progress.Reported);
    }

    [Fact]
    public async Task Cancelling_stops_reading_and_the_session_stays_usable()
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var progress = new Steps(step =>
        {
            if (step == "Constraints")
            {
                cancel.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Reader.ReadSnapshotAsync(Owner, progress, cancel.Token));

        Assert.NotEmpty(await Reader.GetTablesAsync(Owner, Ct));
    }

    /// <summary>Compares records with lists by content (record equality compares lists by reference).</summary>
    private static void AssertSame<T>(IReadOnlyList<T> expected, IReadOnlyList<T> actual, string table) =>
        Assert.True(
            JsonSerializer.Serialize(expected) == JsonSerializer.Serialize(actual),
            $"{table} ({typeof(T).Name}):\nexpected {JsonSerializer.Serialize(expected)}\nactual   {JsonSerializer.Serialize(actual)}");

    /// <summary>Reports synchronously (<see cref="Progress{T}"/> would post them to the thread pool).</summary>
    private sealed class Steps(Action<string>? onReport = null) : IProgress<string>
    {
        public List<string> Reported { get; } = [];

        public void Report(string value)
        {
            Reported.Add(value);
            onReport?.Invoke(value);
        }
    }
}
