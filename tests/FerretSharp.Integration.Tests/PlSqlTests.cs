using FerretSharp.Core.Oracle;
using FerretSharp.Core.Schema;

namespace FerretSharp.Integration.Tests;

/// <summary>Stored PL/SQL (WP-28): object list, source, parameters, errors, dependencies, synonyms and source search.</summary>
public sealed class PlSqlTests(OracleContainerFixture oracle) : IAsyncLifetime
{
    private OracleSession? _session;
    private string _owner = "";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private OracleSchemaReader Reader => new(_session!);

    private async Task<PlSqlObjectSummary> UnitAsync(string name, PlSqlKind? kind = null) =>
        Assert.Single(await Reader.GetPlSqlObjectsAsync(_owner, Ct), o => o.Name == name && (kind is null || o.Kind == kind));

    public async ValueTask InitializeAsync()
    {
        var (profile, password) = oracle.RequireProfile();
        await PlSqlSchema.EnsureCreatedAsync(oracle.RequireConnectionString(), Ct);
        _owner = profile.EffectiveSchema;
        _session = await OracleSession.OpenAsync(
            OracleConnectionStringFactory.Create(profile, password), new SessionContext("FerretSharp", "PL/SQL tests"), Ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_session is not null)
        {
            await _session.DisposeAsync();
        }
    }

    [Fact]
    public async Task Lists_units_with_status_one_entry_per_package()
    {
        var units = (await Reader.GetPlSqlObjectsAsync(_owner, Ct)).Where(o => o.Name.StartsWith("PL_", StringComparison.Ordinal)).ToList();

        Assert.Equal(
            [("PL_GEHEIM", PlSqlKind.Procedure), ("PL_GROSS", PlSqlKind.Package), ("PL_HEUTE", PlSqlKind.Function), ("PL_KAPUTT", PlSqlKind.Package),
             ("PL_KUNDE_AUS", PlSqlKind.Trigger), ("PL_KUNDE_BIU", PlSqlKind.Trigger), ("PL_LEER", PlSqlKind.Procedure), ("PL_RECHNUNG", PlSqlKind.Package)],
            units.Select(o => (o.Name, o.Kind)));
        var rechnung = units.Single(o => o.Name == "PL_RECHNUNG");
        Assert.Equal(("VALID", "VALID"), (rechnung.Status, rechnung.BodyStatus));
        var kaputt = units.Single(o => o.Name == "PL_KAPUTT");
        Assert.Equal(("VALID", "INVALID"), (kaputt.Status, kaputt.BodyStatus));
        Assert.True(kaputt.IsInvalid);
        Assert.Null(units.Single(o => o.Name == "PL_GROSS").BodyStatus);
        Assert.True(units.Single(o => o.Name == "PL_KUNDE_AUS").IsDisabled);
        Assert.False(units.Single(o => o.Name == "PL_KUNDE_BIU").IsDisabled);
    }

    [Fact]
    public async Task Source_has_one_line_per_dictionary_line()
    {
        var rechnung = await UnitAsync("PL_RECHNUNG");

        var spec = await Reader.GetSourceAsync(rechnung, PlSqlPart.Spec, Ct);
        var body = await Reader.GetSourceAsync(rechnung, PlSqlPart.Body, Ct);

        Assert.StartsWith("PACKAGE PL_RECHNUNG AS", spec.Lines[0], StringComparison.OrdinalIgnoreCase);
        Assert.Equal("  -- Größe ändern: Umlaute bleiben erhalten", spec.Lines[1]);
        Assert.Equal("END PL_RECHNUNG;", spec.Lines[^1]);
        Assert.Equal(9, spec.Lines.Count);
        Assert.All(spec.Lines, l => Assert.DoesNotContain('\n', l));
        Assert.All(spec.Lines, l => Assert.DoesNotContain('\r', l));
        Assert.StartsWith("PACKAGE BODY PL_RECHNUNG AS", body.Lines[0], StringComparison.OrdinalIgnoreCase);
        Assert.False(spec.IsWrapped);
    }

    [Fact]
    public async Task A_large_package_comes_complete_and_in_order()
    {
        var source = await Reader.GetSourceAsync(await UnitAsync("PL_GROSS"), PlSqlPart.Spec, Ct);

        Assert.Equal(PlSqlSchema.LargeLines, source.Lines.Count);
        Assert.Equal("  C1500 CONSTANT NUMBER := 1500;", source.Lines[1499]);
        Assert.Equal("END PL_GROSS;", source.Lines[^1]);
    }

    [Fact]
    public async Task Wrapped_source_is_recognized()
    {
        var source = await Reader.GetSourceAsync(await UnitAsync("PL_GEHEIM"), PlSqlPart.Spec, Ct);

        Assert.True(source.IsWrapped);
    }

    [Fact]
    public async Task Parameters_by_subprogram_with_overloads_return_types_and_defaults()
    {
        var subprograms = await Reader.GetSubprogramsAsync(await UnitAsync("PL_RECHNUNG"), Ct);

        Assert.Equal(
            [("BERECHNE", (int?)1), ("BERECHNE", 2), ("BUCHE", null), ("AUFRAEUMEN", null), ("LIES", null)],
            subprograms.Select(s => (s.Name, s.Overload)));

        var first = subprograms[0];
        Assert.Equal("NUMBER", first.ReturnType);
        Assert.Equal([new PlSqlParameter("P_BETRAG", PlSqlDirection.In, "NUMBER", false)], first.Parameters);
        Assert.True(subprograms[1].Parameters.Single(p => p.Name == "P_WAEHRUNG").HasDefault);

        var buche = subprograms[2];
        Assert.Null(buche.ReturnType);
        Assert.Equal(
            [new PlSqlParameter("P_ID", PlSqlDirection.In, "NUMBER", false), new PlSqlParameter("P_ERGEBNIS", PlSqlDirection.Out, "VARCHAR2", false),
             new PlSqlParameter("P_ZAEHLER", PlSqlDirection.InOut, "PLS_INTEGER", false)],
            buche.Parameters);

        Assert.Empty(subprograms[3].Parameters); // AUFRAEUMEN: only the placeholder row

        var lies = subprograms[4].Parameters.ToDictionary(p => p.Name, p => p.Type);
        Assert.Equal("PL_KUNDE%ROWTYPE", lies["P_KUNDE"]);
        Assert.Equal("PL_RECHNUNG.T_POSTEN", lies["P_POSTEN"]);
        Assert.Equal("SYS_REFCURSOR", lies["P_CURSOR"]);
        Assert.Equal("BOOLEAN", lies["P_OK"]);
    }

    [Fact]
    public async Task Standalone_function_without_parameters_and_trigger_without_subprograms()
    {
        var heute = Assert.Single(await Reader.GetSubprogramsAsync(await UnitAsync("PL_HEUTE"), Ct));
        Assert.Equal(("PL_HEUTE", "DATE"), (heute.Name, heute.ReturnType));
        Assert.Empty(heute.Parameters);

        var leer = Assert.Single(await Reader.GetSubprogramsAsync(await UnitAsync("PL_LEER"), Ct));
        Assert.Empty(leer.Parameters);

        Assert.Empty(await Reader.GetSubprogramsAsync(await UnitAsync("PL_KUNDE_BIU"), Ct));
    }

    [Fact]
    public async Task Compile_errors_point_into_the_body()
    {
        var kaputt = await UnitAsync("PL_KAPUTT");

        var errors = await Reader.GetErrorsAsync(kaputt, Ct);
        var body = await Reader.GetSourceAsync(kaputt, PlSqlPart.Body, Ct);

        var error = Assert.Single(errors, e => e.Text.StartsWith("PLS-00201", StringComparison.Ordinal));
        Assert.Equal((PlSqlPart.Body, PlSqlSchema.BrokenLine, false), (error.Part, error.Line, error.IsWarning));
        Assert.StartsWith("X_UNBEKANNT", body.Lines[error.Line - 1][(error.Column - 1)..], StringComparison.Ordinal);
        Assert.Empty(await Reader.GetErrorsAsync(await UnitAsync("PL_RECHNUNG"), Ct));
    }

    [Fact]
    public async Task Info_has_status_dates_authid_and_trigger_details()
    {
        var rechnung = await Reader.GetPlSqlInfoAsync(await UnitAsync("PL_RECHNUNG"), Ct);
        Assert.Equal(("VALID", "VALID", "DEFINER"), (rechnung.Status, rechnung.BodyStatus, rechnung.AuthId));
        Assert.NotNull(rechnung.Created);
        Assert.NotNull(rechnung.BodyLastDdl);
        Assert.Null(rechnung.Trigger);

        var trigger = (await Reader.GetPlSqlInfoAsync(await UnitAsync("PL_KUNDE_BIU"), Ct)).Trigger;
        Assert.NotNull(trigger);
        Assert.Equal(("BEFORE EACH ROW", "INSERT OR UPDATE", "TABLE", true), (trigger.Timing, trigger.Event, trigger.BaseObjectType, trigger.Enabled));
        Assert.Equal(new TableRef(_owner, "PL_KUNDE"), trigger.Table);
        Assert.Equal("NEW.NAME IS NOT NULL", trigger.When);

        var disabled = (await Reader.GetPlSqlInfoAsync(await UnitAsync("PL_KUNDE_AUS"), Ct)).Trigger;
        Assert.Equal(("AFTER STATEMENT", "DELETE", false), (disabled!.Timing, disabled.Event, disabled.Enabled));
    }

    [Fact]
    public async Task Dependencies_of_units_and_of_their_tables()
    {
        var rechnung = await Reader.GetDependenciesAsync(await UnitAsync("PL_RECHNUNG"), Ct);
        Assert.Contains(rechnung.Uses, d => d is { Name: "PL_KUNDE", Type: "TABLE" });
        Assert.DoesNotContain(rechnung.Uses, d => d.Name == "PL_RECHNUNG"); // body → own spec
        Assert.Contains(rechnung.UsedBy, d => d is { Name: "PL_LEER", Type: "PROCEDURE" });
        Assert.DoesNotContain(rechnung.UsedBy, d => d.Name == "PL_RECHNUNG"); // own body

        var leer = await Reader.GetDependenciesAsync(await UnitAsync("PL_LEER"), Ct);
        Assert.Contains(leer.Uses, d => d is { Name: "PL_RECHNUNG", Type: "PACKAGE" });

        var table = await Reader.GetDependenciesAsync(new TableSummary(_owner, "PL_KUNDE", TableKind.Table), Ct);
        Assert.Contains(table.UsedBy, d => d is { Name: "PL_RECHNUNG", Type: "PACKAGE BODY" });
        Assert.Contains(table.UsedBy, d => d is { Name: "PL_KUNDE_BIU", Type: "TRIGGER" });
    }

    [Fact]
    public async Task Source_search_finds_lines_ignoring_case_and_treats_wildcards_literally()
    {
        var hits = await Reader.SearchSourceAsync(_owner, "p_betrag * 1.19", 100, Ct);
        var hit = Assert.Single(hits);
        Assert.Equal((new PlSqlRef(_owner, "PL_RECHNUNG", PlSqlKind.Package), PlSqlPart.Body, 2), (hit.Object, hit.Part, hit.Line));
        Assert.Contains("RETURN P_BETRAG * 1.19", hit.Text, StringComparison.Ordinal);

        Assert.Contains(await Reader.SearchSourceAsync(_owner, ":new.geaendert", 100, Ct), h => h.Object.Kind == PlSqlKind.Trigger);
        Assert.Empty(await Reader.SearchSourceAsync(_owner, "P_BETRAG_", 100, Ct)); // "_" is no wildcard
        Assert.Equal(5, (await Reader.SearchSourceAsync(_owner, "CONSTANT NUMBER", 5, Ct)).Count);
    }

    [Fact]
    public async Task Units_of_other_schemas_through_synonyms_show_their_specification_only()
    {
        Assert.SkipUnless(PlSqlSchema.OtherSchemaAvailable, SynonymSchema.UnavailableReason ?? "Other schema not created.");

        var targets = (await Reader.GetSynonymTargetsAsync(_owner, Ct)).PlSql;

        var druck = Assert.Single(targets, t => t.Synonym?.Name == "S_DRUCK");
        Assert.Equal((SynonymSchema.OtherOwner, "PL_DRUCK", PlSqlKind.Package), (druck.Owner, druck.Name, druck.Kind));
        Assert.DoesNotContain(targets, t => t.Name == "PL_INTERN"); // no grant
        Assert.Contains("PROCEDURE DRUCKE", (await Reader.GetSourceAsync(druck, PlSqlPart.Spec, Ct)).Text, StringComparison.Ordinal);
        Assert.True((await Reader.GetSourceAsync(druck, PlSqlPart.Body, Ct)).IsEmpty); // EXECUTE does not show the body
        Assert.Single(await Reader.GetSubprogramsAsync(druck, Ct));

        var cache = new SchemaCache(Reader, _owner);
        await cache.LoadAsync(Ct);
        Assert.Equal("S_DRUCK", cache.FindPlSql(druck.Ref)!.DisplayName);
    }
}
