using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Data;
using FerretSharp.Core.Oracle;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;
using FerretSharp.Core.Settings;
using Oracle.ManagedDataAccess.Client;

namespace FerretSharp.Integration.Tests;

/// <summary>
/// The C# model laid over a real schema (WP-12): a navigation without FK constraint is navigable like a declared one,
/// enum and converted bool values read as their members, and filters on members query the stored values.
/// </summary>
public sealed class ClrModelEnrichmentTests(OracleContainerFixture oracle) : IAsyncLifetime
{
    private static readonly SemaphoreSlim SetupGate = new(1, 1);
    private static bool _created;

    private static readonly string[] Setup =
    [
        "CREATE TABLE ENR_MITARBEITER (ID NUMBER(10) PRIMARY KEY, NAME VARCHAR2(50))",
        // No constraint on BEARBEITER_ID: only the C# model knows the relationship.
        """
        CREATE TABLE ENR_AUFGABE (
            ID            NUMBER(10) PRIMARY KEY,
            BEARBEITER_ID NUMBER(10),
            ART           NUMBER(2) NOT NULL,
            ERLEDIGT      CHAR(1) NOT NULL)
        """,
        "INSERT INTO ENR_MITARBEITER VALUES (7, 'Erika')",
        "INSERT INTO ENR_MITARBEITER VALUES (8, 'Max')",
        "INSERT INTO ENR_AUFGABE VALUES (1, 7, 1, 'N')",
        "INSERT INTO ENR_AUFGABE VALUES (2, 7, 2, 'J')",
        "INSERT INTO ENR_AUFGABE VALUES (3, 8, 2, 'N')",
        "INSERT INTO ENR_AUFGABE VALUES (4, NULL, 9, 'N')",
        "COMMIT",
    ];

    private OracleSession? _session;
    private SchemaCache _schema = null!;
    private OracleDataAccess _data = null!;
    private ClrModelMapping _mapping = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PropertyExport Prop(string name, string column, string type = "int", IReadOnlyList<ValueMapping>? values = null,
        string? converter = null, string? provider = null, bool nullable = false) =>
        new(name, type, type, nullable, false, column, null, converter, provider, false, values);

    private static ModelExport Model() => new(1, "8.0.0", "Shop.Ctx", "options", null,
    [
        new EntityExport("Shop.Mitarbeiter", "Shop.Mitarbeiter", false, null, "ENR_MITARBEITER", null, null, null,
            // Drift: required and 100 characters in the model, NAME VARCHAR2(50) allowing NULL in the database.
            [Prop("Id", "ID"), Prop("Name", "NAME", "string") with { MaxLength = 100 }], ["Id"], []),
        new EntityExport("Shop.Aufgabe", "Shop.Aufgabe", false, null, "ENR_AUFGABE", null, null, null,
            [
                Prop("Id", "ID"),
                Prop("BearbeiterId", "BEARBEITER_ID", nullable: true),
                Prop("Art", "ART", "Aufgabenart", [new("Intern", "1", "1"), new("Kunde", "2", "2")], provider: "int"),
                Prop("Erledigt", "ERLEDIGT", "bool", [new("false", "False", "N"), new("true", "True", "J")], "JaNeinConverter", "string"),
            ],
            ["Id"],
            [new ForeignKeyExport(["BearbeiterId"], "Shop.Mitarbeiter", ["Id"], "Bearbeiter", "Aufgaben", false)]),
    ]);

    public async ValueTask InitializeAsync()
    {
        var (profile, password) = oracle.RequireProfile();
        await SetupGate.WaitAsync(Ct);
        try
        {
            if (!_created)
            {
                await using var connection = new OracleConnection(oracle.RequireConnectionString());
                await connection.OpenAsync(Ct);
                foreach (var statement in Setup)
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = statement;
                    await command.ExecuteNonQueryAsync(Ct);
                }

                _created = true;
            }
        }
        finally
        {
            SetupGate.Release();
        }

        _session = await OracleSession.OpenAsync(
            OracleConnectionStringFactory.Create(profile, password), new SessionContext("FerretSharp", "Model tests"), Ct);
        _data = new OracleDataAccess(_session);
        _schema = new SchemaCache(new OracleSchemaReader(_session), profile.EffectiveSchema);
        await _schema.LoadAsync(Ct);
        _mapping = await ClrModelMapping.BuildAsync(Model(), _schema, Ct);
        _schema.SetForeignKeys(FkSource.ClrModel, _mapping.ForeignKeys);
    }

    public async ValueTask DisposeAsync()
    {
        if (_session is not null)
        {
            await _session.DisposeAsync();
        }
    }

    private async Task<TableDetails> DetailsAsync(string table) =>
        await _schema.GetDetailsAsync(_schema.Find(new TableRef(_schema.Owner, table))!, Ct);

    private async Task<IReadOnlyList<RowData>> ReadAsync(TableDetails table, params FilterCondition[] filters) =>
        (await _data.ReadPageAsync(table, filters, [new SortSpec("ID", false)], new PageSpec(0, 100), Ct)).Rows;

    [Fact]
    public async Task A_model_relationship_without_constraint_navigates_both_ways()
    {
        var aufgabe = await DetailsAsync("ENR_AUFGABE");
        var mitarbeiter = await DetailsAsync("ENR_MITARBEITER");
        var row = (await ReadAsync(aufgabe, FilterCondition.Of("ID", FilterOperator.Equals, "2"))).Single();

        var outgoing = Assert.Single(FkNavigation.JumpsFor(aufgabe, row, _schema.OutgoingOf(aufgabe.Table.Ref), []));
        Assert.Equal((FkSource.ClrModel, "Aufgabe.Bearbeiter", "ID = 7"), (outgoing.ForeignKey.Source, outgoing.ForeignKey.Name, outgoing.Condition));
        Assert.Equal("Erika", Assert.Single(await ReadAsync(mitarbeiter, [.. outgoing.Filters])).Values[1]);

        var erika = (await ReadAsync(mitarbeiter, FilterCondition.Of("ID", FilterOperator.Equals, "7"))).Single();
        var incoming = Assert.Single(FkNavigation.JumpsFor(mitarbeiter, erika, [], _schema.IncomingOf(mitarbeiter.Table.Ref)));
        Assert.Equal(2, await FkNavigation.CountAsync(_data, aufgabe, incoming, FkNavigation.CountTimeout, Ct));
    }

    [Fact]
    public async Task Stored_values_read_as_members_and_member_filters_query_the_stored_values()
    {
        var aufgabe = await DetailsAsync("ENR_AUFGABE");
        var presentation = TablePresentation.Create(aufgabe, _mapping, ClrNameDisplay.Beside);

        var rows = await ReadAsync(aufgabe);
        Assert.Equal(
            [("Intern (1)", "false"), ("Kunde (2)", "true"), ("Kunde (2)", "false"), ("9", "false")],
            rows.Select(r => (presentation.Present(2, r.Values[2]).Text, presentation.Present(3, r.Values[3]).Text)));
        Assert.True(presentation.Present(2, rows[3].Values[2]).Unknown);

        // The filter takes the members' database values (as the member list writes them).
        var kunde = presentation.ValuesOf(2)!.Options.Single(o => o.Name == "Kunde").Value;
        var erledigt = presentation.ValuesOf(3)!.Options.Single(o => o.Name == "true").Value;
        Assert.Equal([2m, 3m], (await ReadAsync(aufgabe, FilterCondition.Of("ART", FilterOperator.Equals, kunde))).Select(r => r.Values[0]));
        Assert.Equal([2m], (await ReadAsync(aufgabe, FilterCondition.Of("ERLEDIGT", FilterOperator.Equals, erledigt))).Select(r => r.Values[0]));
        var both = presentation.ValuesOf(2)!.Options.Select(o => o.Value).ToArray();
        Assert.Equal([1m, 2m, 3m], (await ReadAsync(aufgabe, FilterCondition.Of("ART", FilterOperator.In, both))).Select(r => r.Values[0]));
    }

    [Fact]
    public async Task A_member_picked_in_the_editor_is_written_as_its_stored_value()
    {
        var aufgabe = await DetailsAsync("ENR_AUFGABE");
        var presentation = TablePresentation.Create(aufgabe, _mapping, ClrNameDisplay.Beside);
        var row = (await ReadAsync(aufgabe, FilterCondition.Of("ID", FilterOperator.Equals, "1"))).Single();
        var tracker = new ChangeTracker(aufgabe);

        var art = tracker.SetValue(row, 2, presentation.ValuesOf(2)!.Options.Single(o => o.Name == "Kunde").Value);
        var erledigt = tracker.SetValue(row, 3, presentation.ValuesOf(3)!.Options.Single(o => o.Name == "true").Value);

        Assert.True(art.IsValid && erledigt.IsValid);
        var change = Assert.Single(tracker.Changes);
        Assert.Equal(2m, change.ValueOf(2));
        Assert.Equal("J", change.ValueOf(3));
        Assert.Equal("Kunde (2)", presentation.Present(2, change.ValueOf(2)).Text);
    }

    [Fact]
    public void Columns_that_do_not_fit_their_property_are_found_in_the_dictionary()
    {
        Assert.Equal(
            [
                ("Name", ColumnMismatchKind.Nullability, MismatchSeverity.Warning),
                ("Name", ColumnMismatchKind.Length, MismatchSeverity.Warning),
            ],
            _mapping.ColumnMismatches.Select(m => (m.Property.Name, m.Kind, m.Severity)));
        Assert.Equal("VARCHAR2(50)", _mapping.ColumnMismatches[0].Column.DisplayType);
    }
}
