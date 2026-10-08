using FerretSharp.Core.ClrModel;

namespace FerretSharp.Integration.Tests;

/// <summary>
/// FerretSharp.ModelHost against the sample project (samples/FerretSharp.SampleModel, built with these tests): a real
/// EF Core 8 model with a naming convention in code, own converters and navigations – no database needed (ADR 0009).
/// </summary>
public sealed class ModelHostTests
{
    private static readonly string Configuration = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
        StringComparison.OrdinalIgnoreCase) ? "Release" : "Debug";

    private static readonly string Root = FindRoot();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    internal static ClrProjectLink SampleLink(string? context = null) =>
        new(Path.Combine(Root, "samples", "FerretSharp.SampleModel.Data", "FerretSharp.SampleModel.Data.csproj"), Configuration, context);

    internal static ModelHostRunner Runner(string culture = "de-DE") =>
        new(Path.Combine(Root, "src", "FerretSharp.ModelHost", "bin", Configuration, "net8.0", "FerretSharp.ModelHost.dll"),
            culture: System.Globalization.CultureInfo.GetCultureInfo(culture));

    private sealed class StepCollector : IProgress<string>
    {
        private readonly List<string> _steps = [];

        public IReadOnlyList<string> Steps
        {
            get
            {
                lock (_steps)
                {
                    return _steps.ToList();
                }
            }
        }

        public void Report(string value)
        {
            lock (_steps)
            {
                _steps.Add(value);
            }
        }
    }

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "FerretSharp.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Repository root not found.");
    }

    [Fact]
    public void The_sample_build_output_is_found_with_its_runtime_and_packages()
    {
        var output = BuildOutputLocator.Find(SampleLink());

        Assert.EndsWith("FerretSharp.SampleModel.Data.dll", output.Assembly);
        Assert.Equal(new Version(8, 0), output.TargetFramework);
        Assert.NotEmpty(output.PackageFolders);
        Assert.Empty(output.SharedFrameworks);
    }

    [Fact]
    public async Task Reads_the_sample_model_with_naming_convention_converters_and_navigations()
    {
        var link = SampleLink();
        var steps = new StepCollector();
        var result = await Runner().ReadModelAsync(link, BuildOutputLocator.Find(link), Ct, steps);

        Assert.Null(result.Error);
        // The host reports its steps on stdout as they happen (shown on the model page with their durations).
        Assert.Equal(["Starte den Hilfsprozess", "Lade die Assemblies", "Erzeuge den DbContext", "Baue das Modell (OnModelCreating)", "Lese das Modell aus"],
            steps.Steps);
        var model = result.Model!;
        Assert.StartsWith("8.0.", model.EfVersion);
        Assert.Equal("options", model.CreatedBy); // our options and the DbContextOptions constructor, no start-up code
        Assert.Equal("FerretSharp.SampleModel.Data.AppDbContext", model.ContextType);

        var kunde = model.Entities.Single(e => e.ClrType == "FerretSharp.SampleModel.Entities.Kunde");
        Assert.Equal("KUNDEN", kunde.Table); // DbSet name, upper-cased by the convention in code
        Assert.Null(kunde.Schema);
        Assert.Equal(["KundeId"], kunde.PrimaryKey);
        Assert.Equal("Kunden", kunde.DbSet);
        Assert.Equal("KUNDE_ID", kunde.Properties.Single(p => p.Name == "KundeId").Column);
        Assert.Equal("ERSTELLT_AM", kunde.Properties.Single(p => p.Name == "ErstelltAm").Column);

        // Configured facets for the comparison with the columns; nothing configured stays null (not the provider's default).
        var name = kunde.Properties.Single(p => p.Name == "Name");
        Assert.Equal((100, false, false), (name.MaxLength, name.Nullable, name.ColumnNullable));
        var umsatz = kunde.Properties.Single(p => p.Name == "Umsatz");
        Assert.Equal((12, 2, true), (umsatz.Precision, umsatz.Scale, umsatz.ColumnNullable));
        Assert.Equal(3, kunde.Properties.Single(p => p.Name == "Kuerzel").MaxLength);
        var erstelltAm = kunde.Properties.Single(p => p.Name == "ErstelltAm");
        Assert.Equal(((int?)null, (int?)null, (int?)null), (erstelltAm.MaxLength, erstelltAm.Precision, erstelltAm.Scale));

        var gesperrt = kunde.Properties.Single(p => p.Name == "Gesperrt");
        Assert.Equal(("bool", "JaNeinConverter", "string"), (gesperrt.ClrType, gesperrt.Converter, gesperrt.ProviderClrType));
        Assert.Equal(1, gesperrt.MaxLength); // from HasColumnType("CHAR(1)")
        Assert.Equal([new ValueMapping("false", "False", "N"), new ValueMapping("true", "True", "J")], gesperrt.Values);

        var kundenart = kunde.Properties.Single(p => p.Name == "Kundenart");
        Assert.Null(kundenart.Converter); // EF's own enum-to-number mapping
        Assert.Equal(["Privat=1", "Gewerbe=2", "Behoerde=3"], kundenart.Values!.Select(v => $"{v.Name}={v.ProviderValue}"));

        var auftrag = model.Entities.Single(e => e.Name.EndsWith(".Auftrag", StringComparison.Ordinal));
        Assert.Equal("AUFTRAG", auftrag.Table);
        Assert.Equal("Auftraege", auftrag.DbSet);
        var status = auftrag.Properties.Single(p => p.Name == "Status");
        Assert.Equal("UpperCaseEnumConverter<AuftragStatus>", status.Converter);
        Assert.Equal(["Offen=OFFEN", "Versandt=VERSANDT", "Storniert=STORNIERT", "Abgeschlossen=ABGESCHLOSSEN"],
            status.Values!.Select(v => $"{v.Name}={v.ProviderValue}"));
        var toKunde = auftrag.ForeignKeys.Single(f => f.PrincipalEntity.EndsWith(".Kunde", StringComparison.Ordinal));
        Assert.Equal(["KundeId"], toKunde.Properties);
        Assert.Equal(("Kunde", "Auftraege"), (toKunde.Navigation, toKunde.InverseNavigation));
        // A navigation without FK constraint in the database (WP-12: navigable "aus C#-Modell").
        var toBearbeiter = auftrag.ForeignKeys.Single(f => f.PrincipalEntity.EndsWith(".Mitarbeiter", StringComparison.Ordinal));
        Assert.Equal(["BearbeiterId"], toBearbeiter.Properties);
        Assert.Equal(["Id"], toBearbeiter.PrincipalProperties);
        Assert.Equal("Bearbeiter", toBearbeiter.Navigation);
        Assert.Equal("BEARBEITER_ID", auftrag.Properties.Single(p => p.Name == "BearbeiterId").Column);

        var position = model.Entities.Single(e => e.Name.EndsWith(".AuftragPosition", StringComparison.Ordinal));
        Assert.Equal("AUFTRAG_POSITION", position.Table);
        Assert.Equal(["AuftragId", "PosNr"], position.PrimaryKey);

        // An entity on a view; the convention gives every entity a table name, so it has both (as in the user's project).
        var view = model.Entities.Single(e => e.Name.EndsWith(".KundeAuftraegeView", StringComparison.Ordinal));
        Assert.Equal(("V_KUNDEN_AUFTRAEGE", "KUNDE_AUFTRAEGE_VIEW"), (view.View, view.Table));
        Assert.Equal("KUNDE_ID", view.Properties.Single(p => p.Name == "KundeId").ViewColumn);
        Assert.Null(view.DbSet); // configured without a DbSet property

        // Generic base configuration: the shared master data columns.
        var kategorie = model.Entities.Single(e => e.Name.EndsWith(".Kategorie", StringComparison.Ordinal));
        Assert.Equal("KATEGORIE", kategorie.Table);
        Assert.Contains(kategorie.Properties, p => p is { Name: "GeaendertAm", Column: "GEAENDERT_AM", Nullable: true });
    }

    [Fact]
    public async Task An_unknown_context_type_is_reported()
    {
        var link = SampleLink("GibtEsNicht");
        var result = await Runner().ReadModelAsync(link, BuildOutputLocator.Find(link), Ct);

        Assert.Null(result.Model);
        Assert.Equal(ModelHostErrorKind.NoContext, result.Error!.Kind);
        Assert.Contains("GibtEsNicht", result.Error.Message);
    }

    [Fact]
    public async Task A_missing_model_host_is_reported_not_thrown()
    {
        var link = SampleLink();
        var result = await new ModelHostRunner(Path.Combine(Root, "gibt-es-nicht.dll")).ReadModelAsync(link, BuildOutputLocator.Find(link), Ct);

        Assert.Equal(ClrModelErrorKind.HostFailed, result.Error!.Kind);
    }

    [Theory]
    [InlineData("de-DE", "Versendet")]
    [InlineData("en-US", "Shipped")]
    public async Task Enum_display_names_come_from_the_projects_resources_in_the_ui_culture(string culture, string versandt)
    {
        var link = SampleLink();
        var result = await Runner(culture).ReadModelAsync(link, BuildOutputLocator.Find(link), Ct);

        var model = result.Model!;
        var kundenart = model.Entities.Single(e => e.ClrType.EndsWith(".Kunde", StringComparison.Ordinal)).Properties.Single(p => p.Name == "Kundenart");
        // [Display(Name = "Behörde")] without resources; members without the attribute have none.
        Assert.Equal([null, null, "Behörde"], kundenart.Values!.Select(v => v.DisplayName));

        // [Display(ResourceType = typeof(EnumTexts), Name = …)]: neutral resources in the assembly, German ones in de\ (satellite).
        var status = model.Entities.Single(e => e.Name.EndsWith(".Auftrag", StringComparison.Ordinal)).Properties.Single(p => p.Name == "Status");
        Assert.Equal<(string, string?, string?)>(("Versandt", "VERSANDT", versandt), status.Values!.Select(v => (v.Name, v.ProviderValue, v.DisplayName)).ElementAt(1));
    }

    /// <summary>WP-16: the real exported model goes through the cache unchanged, for the build output it came from.</summary>
    [Fact]
    public async Task The_exported_model_survives_the_cache()
    {
        var link = SampleLink();
        var output = BuildOutputLocator.Find(link);
        var model = (await Runner().ReadModelAsync(link, output, Ct)).Model!;
        var directory = Directory.CreateTempSubdirectory("ferret-modelcache-").FullName;
        try
        {
            var host = Path.Combine(Root, "src", "FerretSharp.ModelHost", "bin", Configuration, "net8.0", "FerretSharp.ModelHost.dll");
            var cache = new ModelCache(directory, host, System.Globalization.CultureInfo.GetCultureInfo("de-DE"));
            await cache.SaveAsync(link, output, model, Ct);

            var cached = await cache.TryLoadAsync(link, BuildOutputLocator.Find(link), Ct);

            Assert.NotNull(cached);
            Assert.Equal(
                System.Text.Json.JsonSerializer.Serialize(model, ModelHostResult.JsonOptions),
                System.Text.Json.JsonSerializer.Serialize(cached.Model, ModelHostResult.JsonOptions));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
