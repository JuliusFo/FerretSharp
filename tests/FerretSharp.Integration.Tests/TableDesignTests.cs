using FerretSharp.Core.Connections;
using FerretSharp.Core.Data;
using FerretSharp.Core.Design;
using FerretSharp.Core.Oracle;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;
using Oracle.ManagedDataAccess.Client;

namespace FerretSharp.Integration.Tests;

/// <summary>
/// DDL of the table designer (WP-23) run through the schema path against Oracle: what the designer writes runs, and what
/// it refuses beforehand is what Oracle refuses.
/// </summary>
public sealed class TableDesignTests(OracleContainerFixture oracle) : IAsyncLifetime
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static bool _created;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await Gate.WaitAsync(Ct);
        try
        {
            if (_created)
            {
                return;
            }

            await using var connection = new OracleConnection(oracle.RequireConnectionString());
            await connection.OpenAsync(Ct);
            foreach (var sql in new[]
            {
                "CREATE TABLE TD_KUNDE (ID NUMBER(10) PRIMARY KEY, NAME VARCHAR2(50 CHAR) NOT NULL)",
                "CREATE TABLE TD_AUFTRAG (ID NUMBER(10) PRIMARY KEY, KUNDE_ID NUMBER(10) CONSTRAINT FK_TD_AUFTRAG_KUNDE REFERENCES TD_KUNDE, "
                + "\"Nummer\" VARCHAR2(20), DATUM DATE, NOTIZ CLOB)",
                "INSERT INTO TD_KUNDE VALUES (1, 'Eins')",
                "INSERT INTO TD_AUFTRAG VALUES (1, 1, 'A-1', DATE '2026-10-01', NULL)",
                "INSERT INTO TD_AUFTRAG VALUES (2, 1, 'A-1', DATE '2026-10-02', NULL)",
                "COMMIT",
            })
            {
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                await command.ExecuteNonQueryAsync(Ct);
            }

            _created = true;
        }
        finally
        {
            Gate.Release();
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<IDatabaseConnection> OpenAsync(string action)
    {
        var (profile, password) = oracle.RequireProfile();
        return await new OracleDatabaseConnector().OpenAsync(profile, password, action, Ct);
    }

    private TableRef Ref(string table) => new(oracle.RequireProfile().Profile.EffectiveSchema, table);

    private async Task<(TableDetails Details, IReadOnlyList<IndexInfo> Indexes)> ReadAsync(IDatabaseConnection connection, string table)
    {
        var details = await connection.Schema.GetDetailsAsync(new TableSummary(Ref(table).Owner, table, TableKind.Table), Ct);
        return (details, await connection.Schema.GetIndexesAsync(Ref(table), Ct));
    }

    [Fact]
    public async Task Index_for_an_unindexed_foreign_key_runs_and_covers_it()
    {
        await using var connection = await OpenAsync("Design fk index");
        var (details, indexes) = await ReadAsync(connection, "TD_AUFTRAG");
        var foreignKeys = (await connection.Schema.GetForeignKeysAsync([Ref("TD_AUFTRAG").Owner], Ct)).Where(f => f.From == Ref("TD_AUFTRAG")).ToList();
        var fk = Assert.Single(IndexAdvice.UnindexedForeignKeys(foreignKeys, indexes));

        var draft = IndexDesign.ForForeignKey(fk, indexes);
        Assert.True(IndexDesign.Check(draft, details, indexes).CanRun);
        await connection.Editor.ExecuteDdlAsync(IndexDesign.Ddl(draft, Ref("TD_AUFTRAG")), Ct);

        var after = await connection.Schema.GetIndexesAsync(Ref("TD_AUFTRAG"), Ct);
        var created = Assert.Single(after, i => i.Name == "IX_TD_AUFTRAG_KUNDE_ID");
        Assert.False(created.Unique);
        Assert.Empty(IndexAdvice.UnindexedForeignKeys(foreignKeys, after));
        Assert.Equal(TransactionMode.None, connection.Editor.Transaction.Mode);

        // The designer refuses the same column list again – as Oracle does.
        var again = new IndexDraft("IX_TD_AUFTRAG_KUNDE_ID_2", ["KUNDE_ID"]);
        Assert.False(IndexDesign.Check(again, details, after).CanRun);
        var error = await Assert.ThrowsAsync<DatabaseException>(() => connection.Editor.ExecuteDdlAsync(IndexDesign.Ddl(again, Ref("TD_AUFTRAG")), Ct));
        Assert.Equal("ORA-01408", error.ErrorCode);

        await connection.Editor.ExecuteDdlAsync($"DROP INDEX \"{created.Owner}\".\"{created.Name}\"", Ct);
    }

    [Fact]
    public async Task Unique_index_on_a_mixed_case_column_runs_and_unique_on_duplicates_fails()
    {
        await using var connection = await OpenAsync("Design unique index");
        var (details, indexes) = await ReadAsync(connection, "TD_AUFTRAG");

        var unique = new IndexDraft(IndexDesign.SuggestName("TD_AUFTRAG", ["Nummer", "DATUM"], indexes.Select(i => i.Name)), ["Nummer", "DATUM"], Unique: true);
        Assert.Equal("IX_TD_AUFTRAG_NUMMER_DATUM", unique.Name); // a plain name, though the column is "Nummer"
        var check = IndexDesign.Check(unique, details, indexes);
        Assert.True(check.CanRun);
        Assert.Empty(check.Warnings);
        await connection.Editor.ExecuteDdlAsync(IndexDesign.Ddl(unique, Ref("TD_AUFTRAG")), Ct);
        var created = Assert.Single(await connection.Schema.GetIndexesAsync(Ref("TD_AUFTRAG"), Ct), i => i.Name == unique.Name);
        Assert.True(created.Unique);
        Assert.Equal(["Nummer", "DATUM"], created.Columns.Select(c => c.Name));
        await connection.Editor.ExecuteDdlAsync($"DROP INDEX \"{created.Owner}\".\"{created.Name}\"", Ct);

        // Duplicates are Oracle's to find; nothing is left behind.
        var duplicates = new IndexDraft("UX_TD_AUFTRAG_NUMMER", ["Nummer"], Unique: true);
        var error = await Assert.ThrowsAsync<DatabaseException>(() => connection.Editor.ExecuteDdlAsync(IndexDesign.Ddl(duplicates, Ref("TD_AUFTRAG")), Ct));
        Assert.Equal("ORA-01452", error.ErrorCode);
        Assert.DoesNotContain(await connection.Schema.GetIndexesAsync(Ref("TD_AUFTRAG"), Ct), i => i.Name == duplicates.Name);
    }

    /// <summary>
    /// A column draft as the designer builds it (stage 2): the facts come from the read path, the plan refuses what Oracle
    /// would refuse, and what it writes runs through the schema path and leaves the table as drafted.
    /// </summary>
    [Fact]
    public async Task A_column_draft_runs_and_leaves_the_table_as_drafted()
    {
        await using var connection = await OpenAsync("Design columns");
        foreach (var sql in new[]
        {
            "CREATE TABLE TD_PERSON (ID NUMBER(10) PRIMARY KEY, NAME VARCHAR2(50 BYTE), KUERZEL VARCHAR2(10 BYTE), BETRAG NUMBER(12,2), NOTIZ VARCHAR2(200), ALT NUMBER)",
            "COMMENT ON COLUMN TD_PERSON.NOTIZ IS 'weg damit'",
        })
        {
            await connection.Editor.ExecuteDdlAsync(sql, Ct);
        }

        await connection.Editor.ExecuteAsync(new QuerySpec("INSERT INTO TD_PERSON (ID, NAME, KUERZEL, BETRAG) VALUES (1, 'Eins', 'E', 10.5)", []), Ct);
        await connection.Editor.ExecuteAsync(new QuerySpec("INSERT INTO TD_PERSON (ID, NAME, BETRAG) VALUES (2, NULL, 3)", []), Ct);
        await connection.Editor.CommitAsync(Ct);
        var (before, _) = await ReadAsync(connection, "TD_PERSON");
        ColumnDraft Change(string column, Func<ColumnInfo, ColumnInfo> change) => new(column, change(before.Columns[before.IndexOf(column)]));

        // NAME has a NULL, BETRAG values: NOT NULL and fewer integer digits are refused, as Oracle would.
        var refused = new TableDraft([.. TableDraft.Of(before).Columns.Select(c => c.Original switch
        {
            "NAME" => Change("NAME", n => n with { Nullable = false }),
            "BETRAG" => Change("BETRAG", n => n with { Precision = 8 }),
            _ => c,
        })]);
        var facts = await TableDesign.CountAsync(connection.Data, before, TableDesign.Questions(before, refused), Ct);
        var refusedPlan = TableDesign.Plan(DesignContext.Of(before), refused, facts);
        Assert.Equal(2, refusedPlan.Problems.Count);
        Assert.False(refusedPlan.CanRun);

        // What runs: rename with a longer type, more digits, a default, drop, a new mandatory column with default, comments.
        var draft = new TableDraft([.. TableDraft.Of(before).Columns.Select(c => c.Original switch
        {
            "KUERZEL" => Change("KUERZEL", k => k with { Name = "CODE", Length = 20, CharSemantics = true, Comment = "Kurzzeichen" }),
            "BETRAG" => Change("BETRAG", b => b with { Precision = 14, Scale = 4, Default = "0" }),
            "NOTIZ" => c with { Dropped = true },
            "ALT" => Change("ALT", a => a with { DataType = "VARCHAR2", Length = 10, Precision = null, Scale = null }), // empty: may change family
            _ => c,
        }), new ColumnDraft(null, new ColumnInfo("AKTIV", "CHAR", 1, false, null, null, false, false, "'J'", 0, "J/N"))]);
        facts = await TableDesign.CountAsync(connection.Data, before, TableDesign.Questions(before, draft), Ct);
        Assert.Equal(0, facts.Of(new DataQuestion(DataQuestionKind.Values, "ALT")));
        var plan = TableDesign.Plan(new DesignContext(before, await connection.Schema.GetConstraintsAsync(before.Table.Ref, Ct), [], []), draft, facts);
        Assert.True(plan.CanRun, string.Join("\n", plan.Problems));

        foreach (var statement in plan.Statements)
        {
            await connection.Editor.ExecuteDdlAsync(statement, Ct);
        }

        var (after, _) = await ReadAsync(connection, "TD_PERSON");
        Assert.Equal(["ID", "NAME", "CODE", "BETRAG", "ALT", "AKTIV"], after.Columns.Select(c => c.Name));
        ColumnInfo Col(string name) => after.Columns[after.IndexOf(name)];
        Assert.Equal(("VARCHAR2(20 CHAR)", "Kurzzeichen"), (Col("CODE").DisplayType, Col("CODE").Comment));
        Assert.Equal(("NUMBER(14,4)", "0"), (Col("BETRAG").DisplayType, Col("BETRAG").Default?.Trim()));
        Assert.Equal("VARCHAR2(10)", Col("ALT").DisplayType);
        Assert.Equal(("CHAR(1)", false, "'J'", "J/N"), (Col("AKTIV").DisplayType, Col("AKTIV").Nullable, Col("AKTIV").Default?.Trim(), Col("AKTIV").Comment));
        var rows = await connection.Data.ReadSqlAsync(new QuerySpec("SELECT code, betrag, aktiv FROM td_person ORDER BY id", []), 0, 10, Ct);
        Assert.Equal(["E", 10.5m, "J"], rows.Rows[0]);

        // The table as it is now: the draft of it writes nothing.
        Assert.Empty(TableDesign.Plan(DesignContext.Of(after), TableDraft.Of(after), DataFacts.None).Statements);
        await connection.Editor.ExecuteDdlAsync("DROP TABLE TD_PERSON", Ct);
    }

    /// <summary>Constraints of the designer against Oracle: new keys, check and foreign key with its index; a dropped constraint frees its column.</summary>
    [Fact]
    public async Task Constraint_changes_run_in_the_order_they_need()
    {
        await using var connection = await OpenAsync("Design constraints");
        foreach (var sql in new[]
        {
            "CREATE TABLE TD_LAND (CODE VARCHAR2(2 BYTE) CONSTRAINT PK_TD_LAND PRIMARY KEY, NAME VARCHAR2(50))",
            "CREATE TABLE TD_ORT (ID NUMBER(10), PLZ VARCHAR2(5), NAME VARCHAR2(50), CONSTRAINT UK_TD_ORT UNIQUE (PLZ, NAME))",
        })
        {
            await connection.Editor.ExecuteDdlAsync(sql, Ct);
        }

        await connection.Editor.ExecuteAsync(new QuerySpec("INSERT INTO TD_LAND VALUES ('DE', 'Deutschland')", []), Ct);
        await connection.Editor.ExecuteAsync(new QuerySpec("INSERT INTO TD_ORT VALUES (1, '12345', 'Musterstadt')", []), Ct);
        await connection.Editor.CommitAsync(Ct);
        var (ort, indexes) = await ReadAsync(connection, "TD_ORT");
        var (land, _) = await ReadAsync(connection, "TD_LAND");
        var context = new DesignContext(ort, await connection.Schema.GetConstraintsAsync(ort.Table.Ref, Ct), indexes, [])
        {
            Referenced = new Dictionary<TableRef, TableDetails> { [land.Table.Ref] = land },
        };

        // PLZ goes – its unique key over two columns is dropped first; a new column LAND with default references TD_LAND.
        var draft = new TableDraft([.. TableDraft.Of(ort).Columns.Select(c => c.Original == "PLZ" ? c with { Dropped = true } : c),
            new ColumnDraft(null, new ColumnInfo("LAND", "VARCHAR2", 2, false, null, null, false, false, "'DE'", 0))])
        {
            DroppedConstraints = ["UK_TD_ORT"],
            AddedConstraints =
            [
                new ConstraintDraft("FK_TD_ORT_LAND", ConstraintType.ForeignKey, ["LAND"], References: land.Table.Ref, ReferencedColumns: ["CODE"]),
                new ConstraintDraft("PK_TD_ORT", ConstraintType.PrimaryKey, ["ID"]),
                new ConstraintDraft("CK_TD_ORT_NAME", ConstraintType.Check, ["NAME"], "NAME IS NOT NULL OR ID < 0"),
            ],
        };
        var facts = await TableDesign.CountAsync(connection.Data, ort, TableDesign.Questions(ort, draft), Ct);
        var plan = TableDesign.Plan(context, draft, facts);
        Assert.True(plan.CanRun, string.Join("\n", plan.Problems));
        foreach (var statement in plan.Statements)
        {
            await connection.Editor.ExecuteDdlAsync(statement, Ct);
        }

        var (after, afterIndexes) = await ReadAsync(connection, "TD_ORT");
        var constraints = await connection.Schema.GetConstraintsAsync(after.Table.Ref, Ct);
        Assert.Equal(["ID", "NAME", "LAND"], after.Columns.Select(c => c.Name));
        Assert.Equal(["ID"], after.PrimaryKey);
        Assert.Contains(constraints, c => c is { Name: "FK_TD_ORT_LAND", Type: ConstraintType.ForeignKey });
        Assert.Contains(constraints, c => c is { Name: "CK_TD_ORT_NAME", Type: ConstraintType.Check });
        Assert.DoesNotContain(constraints, c => c.Name == "UK_TD_ORT");
        Assert.Contains(afterIndexes, i => i.Name == "IX_TD_ORT_LAND");
        var fks = (await connection.Schema.GetForeignKeysAsync([after.Table.Owner], Ct)).Where(f => f.From == after.Table.Ref);
        Assert.Empty(IndexAdvice.UnindexedForeignKeys(fks, afterIndexes));

        await connection.Editor.ExecuteDdlAsync("DROP TABLE TD_ORT", Ct);
        await connection.Editor.ExecuteDdlAsync("DROP TABLE TD_LAND", Ct);
    }

    /// <summary>
    /// CREATE INDEX is no ALTER TABLE: the schema path does not take the table first. With another session's uncommitted
    /// DML it fails at once with ORA-00054 (WP-22) – the designer shows the lock holders as the SQL editor does.
    /// </summary>
    [Fact]
    public async Task Index_on_a_table_another_session_has_written_to_fails_at_once()
    {
        await using var writer = await OpenAsync("Design busy writer");
        await using var connection = await OpenAsync("Design busy ddl");
        await writer.Editor.ExecuteAsync(new QuerySpec("UPDATE td_auftrag SET datum = datum WHERE id = 1", []), Ct);
        var draft = new IndexDraft("IX_TD_AUFTRAG_DATUM", ["DATUM"]);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<DatabaseException>(() => connection.Editor.ExecuteDdlAsync(IndexDesign.Ddl(draft, Ref("TD_AUFTRAG")), Ct));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Took {stopwatch.Elapsed}.");
        Assert.Equal("ORA-00054", error.ErrorCode);

        await writer.Editor.RollbackAsync(Ct);
        await connection.Editor.ExecuteDdlAsync(IndexDesign.Ddl(draft, Ref("TD_AUFTRAG")), Ct);
        await connection.Editor.ExecuteDdlAsync($"DROP INDEX \"{Ref("TD_AUFTRAG").Owner}\".\"IX_TD_AUFTRAG_DATUM\"", Ct);
    }

    /// <summary>
    /// WP-67: a new table, its key's and foreign key's indexes in the chosen tablespaces, then an index with a descending
    /// column, and the same columns ascending next to it – read back as drafted.
    /// </summary>
    [Fact]
    public async Task Tablespaces_and_descending_columns_run_and_read_back_as_drafted()
    {
        await using var connection = await OpenAsync("Design tablespaces");
        var table = Ref("TD_SORT");
        var tablespaces = await connection.Schema.GetTablespacesAsync(Ref("TD_AUFTRAG"), Ct);
        var tablespace = Assert.IsType<string>(tablespaces.Default); // the session's own schema
        Assert.Contains(tablespace, tablespaces.Names);
        Assert.Contains(tablespaces.TableIndexes, u => u.Name == tablespace); // TD_AUFTRAG's key
        Assert.NotEmpty(tablespaces.SchemaIndexes);
        Assert.NotEmpty(tablespaces.SchemaTables);

        var kunde = await connection.Schema.GetDetailsAsync(new TableSummary(table.Owner, "TD_KUNDE", TableKind.Table), Ct);
        ColumnInfo Column(string name, string type, bool nullable) => new(name, type, null, false, type == "NUMBER" ? 10 : null, type == "NUMBER" ? 0 : null, nullable, false, null, 0);
        var draft = new TableDraft([new(null, Column("ID", "NUMBER", false)), new(null, Column("KUNDE_ID", "NUMBER", true)), new(null, Column("DATUM", "DATE", true))])
        {
            AddedConstraints =
            [
                new ConstraintDraft("PK_TD_SORT", ConstraintType.PrimaryKey, ["ID"]),
                new ConstraintDraft("FK_TD_SORT_KUNDE", ConstraintType.ForeignKey, ["KUNDE_ID"], References: kunde.Table.Ref, ReferencedColumns: ["ID"]),
            ],
            IndexTablespace = tablespace,
        };
        var plan = TableDesign.PlanNew(new NewTable(table.Owner, table.Name, Tablespace: tablespace), draft,
            new Dictionary<TableRef, TableDetails> { [kunde.Table.Ref] = kunde }, [], tablespaces);
        Assert.True(plan.CanRun, string.Join(" ", plan.Problems));
        foreach (var sql in plan.Statements)
        {
            await connection.Editor.ExecuteDdlAsync(sql, Ct);
        }

        try
        {
            Assert.Equal(tablespace, (await connection.Schema.GetObjectInfoAsync(new TableSummary(table.Owner, table.Name, TableKind.Table), Ct)).Tablespace);
            Assert.Equal(tablespace, (await connection.Schema.GetTablespacesAsync(table, Ct)).TableTablespace); // where its audit table would go
            var indexes = await connection.Schema.GetIndexesAsync(table, Ct);
            Assert.Equal(["IX_TD_SORT_KUNDE_ID", "PK_TD_SORT"], indexes.Select(i => i.Name).Order());
            Assert.All(indexes, i => Assert.Equal(tablespace, i.Tablespace));

            var details = await connection.Schema.GetDetailsAsync(new TableSummary(table.Owner, table.Name, TableKind.Table), Ct);
            var sorted = new IndexDraft("IX_TD_SORT_DATUM_KUNDE", ["DATUM", "KUNDE_ID"]) { Descending = new HashSet<string> { "DATUM" }, Tablespace = tablespace };
            Assert.True(IndexDesign.Check(sorted, details, indexes, tablespaces: tablespaces).CanRun);
            await connection.Editor.ExecuteDdlAsync(IndexDesign.Ddl(sorted, table), Ct);

            var created = Assert.Single(await connection.Schema.GetIndexesAsync(table, Ct), i => i.Name == sorted.Name);
            Assert.Equal([new IndexColumn("DATUM", false, true), new IndexColumn("KUNDE_ID", false, false)], created.Columns);
            Assert.Equal(tablespace, created.Tablespace);

            // The same columns ascending are another index to Oracle – as the check says.
            var ascending = sorted with { Name = "IX_TD_SORT_DATUM_KUNDE_2", Descending = new HashSet<string>() };
            Assert.True(IndexDesign.Check(ascending, details, [.. indexes, created]).CanRun);
            await connection.Editor.ExecuteDdlAsync(IndexDesign.Ddl(ascending, table), Ct);
        }
        finally
        {
            await connection.Editor.ExecuteDdlAsync($"DROP TABLE \"{table.Owner}\".\"{table.Name}\"", Ct);
        }
    }

    /// <summary>
    /// Review of WP-67: a key whose index was created before it (as SQL Developer and DBMS_METADATA write it) leaves the index
    /// behind when dropped – the designer's new key on the same columns takes it instead of failing with ORA-01408 after the
    /// old key is gone. A temporary table is told apart: its indexes get no tablespace.
    /// </summary>
    [Fact]
    public async Task A_new_key_takes_the_index_a_dropped_key_left_and_a_temporary_table_is_known()
    {
        await using var connection = await OpenAsync("Design key swap");
        var table = Ref("TD_SWAP");
        var temporary = Ref("TD_TEMP");
        foreach (var sql in new[]
        {
            $"CREATE TABLE \"{table.Owner}\".\"TD_SWAP\" (A NUMBER(10), B NUMBER(10))",
            $"CREATE UNIQUE INDEX \"{table.Owner}\".\"UK_TD_SWAP\" ON \"{table.Owner}\".\"TD_SWAP\" (A)",
            $"ALTER TABLE \"{table.Owner}\".\"TD_SWAP\" ADD CONSTRAINT UK_TD_SWAP UNIQUE (A) USING INDEX \"{table.Owner}\".\"UK_TD_SWAP\"",
            $"CREATE GLOBAL TEMPORARY TABLE \"{temporary.Owner}\".\"TD_TEMP\" (A NUMBER(10))",
        })
        {
            await connection.Editor.ExecuteDdlAsync(sql, Ct);
        }

        try
        {
            var details = await connection.Schema.GetDetailsAsync(new TableSummary(table.Owner, table.Name, TableKind.Table), Ct);
            var tablespaces = await connection.Schema.GetTablespacesAsync(table, Ct);
            var context = new DesignContext(details, await connection.Schema.GetConstraintsAsync(table, Ct), await connection.Schema.GetIndexesAsync(table, Ct), [])
            {
                Tablespaces = tablespaces,
            };
            var draft = TableDraft.Of(details) with
            {
                DroppedConstraints = ["UK_TD_SWAP"],
                AddedConstraints = [new ConstraintDraft("PK_TD_SWAP", ConstraintType.PrimaryKey, ["A"])],
                IndexTablespace = Assert.IsType<string>(tablespaces.Default),
            };
            var plan = TableDesign.Plan(context, draft, new DataFacts(new Dictionary<DataQuestion, long> { [new DataQuestion(DataQuestionKind.Rows)] = 0 }));
            Assert.True(plan.CanRun, string.Join(" ", plan.Problems));
            Assert.DoesNotContain(plan.Statements, s => s.Contains("TABLESPACE", StringComparison.Ordinal));
            foreach (var sql in plan.Statements)
            {
                await connection.Editor.ExecuteDdlAsync(sql, Ct);
            }

            var key = Assert.Single(await connection.Schema.GetConstraintsAsync(table, Ct), c => c.Type == ConstraintType.PrimaryKey);
            Assert.Equal(["A"], key.Columns);

            var temp = await connection.Schema.GetTablespacesAsync(temporary, Ct);
            Assert.True(temp.TemporaryTable);
            Assert.Null(temp.TableTablespace);
            Assert.False(tablespaces.TemporaryTable);
        }
        finally
        {
            await connection.Editor.ExecuteDdlAsync($"DROP TABLE \"{table.Owner}\".\"TD_SWAP\"", Ct);
            await connection.Editor.ExecuteDdlAsync($"DROP TABLE \"{temporary.Owner}\".\"TD_TEMP\"", Ct);
        }
    }
}
