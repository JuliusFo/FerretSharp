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

    private static ClrProjectLink SampleLink(string? context = null) =>
        new(Path.Combine(Root, "samples", "FerretSharp.SampleModel.Data", "FerretSharp.SampleModel.Data.csproj"), Configuration, context);

    private static ModelHostRunner Runner() =>
        new(Path.Combine(Root, "src", "FerretSharp.ModelHost", "bin", Configuration, "net8.0", "FerretSharp.ModelHost.dll"));

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
        Assert.Equal("KUNDE_ID", kunde.Properties.Single(p => p.Name == "KundeId").Column);
        Assert.Equal("ERSTELLT_AM", kunde.Properties.Single(p => p.Name == "ErstelltAm").Column);

        var gesperrt = kunde.Properties.Single(p => p.Name == "Gesperrt");
        Assert.Equal(("bool", "JaNeinConverter", "string"), (gesperrt.ClrType, gesperrt.Converter, gesperrt.ProviderClrType));
        Assert.Equal([new ValueMapping("false", "False", "N"), new ValueMapping("true", "True", "J")], gesperrt.Values);

        var kundenart = kunde.Properties.Single(p => p.Name == "Kundenart");
        Assert.Null(kundenart.Converter); // EF's own enum-to-number mapping
        Assert.Equal(["Privat=1", "Gewerbe=2", "Behoerde=3"], kundenart.Values!.Select(v => $"{v.Name}={v.ProviderValue}"));

        var auftrag = model.Entities.Single(e => e.Name.EndsWith(".Auftrag", StringComparison.Ordinal));
        Assert.Equal("AUFTRAG", auftrag.Table);
        var status = auftrag.Properties.Single(p => p.Name == "Status");
        Assert.Equal("UpperCaseEnumConverter<AuftragStatus>", status.Converter);
        Assert.Equal(["Offen=OFFEN", "Versandt=VERSANDT", "Storniert=STORNIERT", "Abgeschlossen=ABGESCHLOSSEN"],
            status.Values!.Select(v => $"{v.Name}={v.ProviderValue}"));
        var toKunde = auftrag.ForeignKeys.Single(f => f.PrincipalEntity.EndsWith(".Kunde", StringComparison.Ordinal));
        Assert.Equal(["KundeId"], toKunde.Properties);
        Assert.Equal(("Kunde", "Auftraege"), (toKunde.Navigation, toKunde.InverseNavigation));

        var position = model.Entities.Single(e => e.Name.EndsWith(".AuftragPosition", StringComparison.Ordinal));
        Assert.Equal("AUFTRAG_POSITION", position.Table);
        Assert.Equal(["AuftragId", "PosNr"], position.PrimaryKey);

        // An entity on a view; the convention gives every entity a table name, so it has both (as in the user's project).
        var view = model.Entities.Single(e => e.Name.EndsWith(".KundeAuftraegeView", StringComparison.Ordinal));
        Assert.Equal(("V_KUNDEN_AUFTRAEGE", "KUNDE_AUFTRAEGE_VIEW"), (view.View, view.Table));
        Assert.Equal("KUNDE_ID", view.Properties.Single(p => p.Name == "KundeId").ViewColumn);

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
}
