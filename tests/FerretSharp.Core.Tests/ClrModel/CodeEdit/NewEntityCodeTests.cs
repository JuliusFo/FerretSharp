using System.Text;
using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Schema;
using FerretSharp.Testing;
using static FerretSharp.Core.Tests.ClrModel.CodeGenerationModel;

namespace FerretSharp.Core.Tests.ClrModel;

/// <summary>A table without entity as new files in the work clone, with DbSet and registration (WP-41, second stage).</summary>
public sealed class NewEntityCodeTests
{
    private static readonly TableDetails Auftrag = new(new TableSummary(Owner, "AUFTRAG", TableKind.Table),
    [
        new("AUFTRAG_ID", "NUMBER", null, false, 10, 0, false, false, null, 1),
        new("KUNDE_ID", "NUMBER", null, false, 10, 0, false, false, null, 2),
        new("BEZAHLT", "CHAR", 1, false, null, null, false, false, null, 3),
    ], ["AUFTRAG_ID"], [], false);

    private static readonly ForeignKeyInfo ToKunde = new("FK_AUFTRAG_KUNDE", Auftrag.Table.Ref, ["KUNDE_ID"], Kunden.Table.Ref, ["KUNDE_ID"], FkSource.Declared);

    private const string Sdk = "<Project Sdk=\"Microsoft.NET.Sdk\" />";

    private static void Write(TestFolder clone, string relative, string text, bool bom = false)
    {
        var path = clone.Combine(relative.Split('/'));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, new UTF8Encoding(bom));
    }

    private static CodeChangePlan Plan(ClrModelMapping mapping, TestFolder clone, string name = "Auftrag") =>
        NewEntityCode.Plan(mapping, Auftrag, [ToKunde], name, CodeScan.Read(clone.Path, NewEntityCode.Words(mapping.Model, name), TestContext.Current.CancellationToken));

    [Fact]
    public async Task The_entity_and_its_configuration_go_where_the_projects_lie_as_their_files_are_written_and_the_context_gets_dbset_and_registration()
    {
        var mapping = await MappingAsync();
        using var clone = new TestFolder();
        Write(clone, "Shop.Entities/Shop.Entities.csproj", Sdk);
        Write(clone, "Shop.Entities/Kunde.cs", "namespace Shop;\r\n\r\npublic class Kunde\r\n{\r\n    public string? Name { get; set; }\r\n}\r\n", bom: true);
        Write(clone, "Shop.Data/Shop.Data.csproj", Sdk);
        Write(clone, "Shop.Data/Converters/JaNeinConverter.cs", "namespace Shop.Data.Converters;\n\npublic class JaNeinConverter;\n");
        Write(clone, "Shop.Data/Configurations/KundeConfiguration.cs", """
            namespace Shop.Data.Configurations;

            public class KundeConfiguration : IEntityTypeConfiguration<Kunde>
            {
              public void Configure(EntityTypeBuilder<Kunde> builder)
              {
                builder.Property(x => x.Gesperrt).HasConversion<JaNeinConverter>();
              }
            }
            """.ReplaceLineEndings("\n"));
        var context = """
            using Microsoft.EntityFrameworkCore;
            using Shop.Data.Configurations;

            namespace Shop;

            public class Ctx : DbContext
            {
                public DbSet<Kunde> Kunden { get; set; } = null!;

                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.ApplyConfiguration(new KundeConfiguration());
                }
            }
            """.ReplaceLineEndings("\n");
        Write(clone, "Shop.Data/Ctx.cs", context);

        var plan = Plan(mapping, clone);

        Assert.True(plan.CanWrite, string.Join("\n", plan.Problems));
        var entity = plan.Files.Single(f => f.Relative == Path.Combine("Shop.Entities", "Auftrag.cs"));
        Assert.True(entity.IsNew);
        Assert.Equal(new CodeFileFormat(CodeFileBom.Utf8, "\r\n", "    "), entity.Format);
        Assert.Equal("""
            namespace Shop;

            public class Auftrag
            {
                public long AuftragId { get; set; }
                public int KundeId { get; set; }
                public bool Bezahlt { get; set; }

                public Kunde Kunde { get; set; } = null!;
            }

            """.ReplaceLineEndings("\r\n"), entity.NewText);
        var configuration = plan.Files.Single(f => f.Relative == Path.Combine("Shop.Data", "Configurations", "AuftragConfiguration.cs"));
        Assert.Equal("""
            using Microsoft.EntityFrameworkCore;
            using Microsoft.EntityFrameworkCore.Metadata.Builders;
            using Shop.Data.Converters;

            namespace Shop.Data.Configurations;

            public class AuftragConfiguration : IEntityTypeConfiguration<Auftrag>
            {
              public void Configure(EntityTypeBuilder<Auftrag> builder)
              {
                builder.ToTable("AUFTRAG");
                builder.HasKey(x => x.AuftragId);
                builder.Property(x => x.Bezahlt).HasConversion<JaNeinConverter>();
                builder.HasOne(x => x.Kunde).WithMany().HasForeignKey(x => x.KundeId);
              }
            }

            """.ReplaceLineEndings("\n"), configuration.NewText);
        Assert.Equal(context
                .Replace("    public DbSet<Kunde> Kunden { get; set; } = null!;\n", "    public DbSet<Kunde> Kunden { get; set; } = null!;\n    public DbSet<Auftrag> Auftrag { get; set; } = null!;\n", StringComparison.Ordinal)
                .Replace("        modelBuilder.ApplyConfiguration(new KundeConfiguration());\n", "        modelBuilder.ApplyConfiguration(new KundeConfiguration());\n        modelBuilder.ApplyConfiguration(new AuftragConfiguration());\n", StringComparison.Ordinal),
            plan.Files.Single(f => f.Relative.EndsWith("Ctx.cs", StringComparison.Ordinal)).NewText);
        Assert.Contains("Kunde bekommt keine Collection von Auftrag – bei Bedarf bitte von Hand ergänzen (und WithMany(x => x.…)).", plan.Notes);
        Assert.DoesNotContain(plan.Notes, n => n.Contains("DbSet", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Without_configuration_classes_it_goes_beside_the_context_with_its_block_namespace_and_tabs_and_assembly_registration_needs_nothing()
    {
        var mapping = await MappingAsync();
        using var clone = new TestFolder();
        Write(clone, "App/App.csproj", "<Project ToolsVersion=\"15.0\"><ItemGroup><Compile Include=\"Kunde.cs\" /></ItemGroup></Project>");
        Write(clone, "App/Kunde.cs", "namespace Shop\n{\n\tpublic class Kunde\n\t{\n\t}\n}\n");
        var context = "namespace Shop\n{\n\tpublic class Ctx : DbContext\n\t{\n\t\tpublic DbSet<Kunde> Kunden => Set<Kunde>();\n\n"
                      + "\t\tprotected override void OnModelCreating(ModelBuilder modelBuilder)\n\t\t{\n"
                      + "\t\t\tmodelBuilder.ApplyConfigurationsFromAssembly(typeof(Ctx).Assembly);\n\t\t}\n\t}\n}\n";
        Write(clone, "App/Ctx.cs", context);

        var plan = Plan(mapping, clone);

        var configuration = plan.Files.Single(f => f.Relative == Path.Combine("App", "AuftragConfiguration.cs"));
        Assert.StartsWith("using Microsoft.EntityFrameworkCore;\nusing Microsoft.EntityFrameworkCore.Metadata.Builders;\n\nnamespace Shop\n{\n\tpublic class AuftragConfiguration", configuration.NewText, StringComparison.Ordinal);
        Assert.Contains("\n\t\t\tbuilder.HasKey(x => x.AuftragId);\n", configuration.NewText, StringComparison.Ordinal);
        Assert.Equal(context.Replace("\t\tpublic DbSet<Kunde> Kunden => Set<Kunde>();\n", "\t\tpublic DbSet<Kunde> Kunden => Set<Kunde>();\n\t\tpublic DbSet<Auftrag> Auftrag => Set<Auftrag>();\n", StringComparison.Ordinal),
            plan.Files.Single(f => f.Relative.EndsWith("Ctx.cs", StringComparison.Ordinal)).NewText);
        Assert.Contains($"{Path.Combine("App", "App.csproj")} ist kein SDK-Projekt: die neuen Dateien bitte von Hand eintragen (Compile Include).", plan.Notes);
    }

    [Fact]
    public async Task Another_folder_of_the_projects_entities_can_be_chosen()
    {
        var mapping = await MappingAsync();
        using var clone = new TestFolder();
        Write(clone, "App/App.csproj", Sdk);
        Write(clone, "App/Kunde.cs", "namespace Shop;\npublic class Kunde;\n");
        Write(clone, "App/Views/KundeView.cs", "namespace Shop;\npublic class KundeView;\n");

        var proposed = Plan(mapping, clone);
        var chosen = NewEntityCode.Plan(mapping, Auftrag, [ToKunde], "Auftrag",
            CodeScan.Read(clone.Path, NewEntityCode.Words(mapping.Model, "Auftrag"), TestContext.Current.CancellationToken), entityFolder: Path.Combine("App", "Views"));

        Assert.Equal([Path.Combine("App"), Path.Combine("App", "Views")], proposed.Places!.EntityFolders.Order(StringComparer.Ordinal));
        Assert.Equal(Path.Combine("App", "Views"), chosen.Places!.EntityFolder);
        Assert.StartsWith("namespace Shop;", chosen.Files.Single(f => f.Relative == Path.Combine("App", "Views", "Auftrag.cs")).NewText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_file_or_type_that_exists_is_never_written_over()
    {
        var mapping = await MappingAsync();
        using var clone = new TestFolder();
        Write(clone, "App/App.csproj", Sdk);
        Write(clone, "App/Kunde.cs", "namespace Shop;\npublic class Kunde;\n");
        Write(clone, "App/Auftrag.cs", "namespace Shop;\npublic class Auftrag;\n");

        var plan = Plan(mapping, clone);

        Assert.False(plan.CanWrite);
        Assert.Equal(
        [
            $"{Path.Combine("App", "Auftrag.cs")} gibt es schon – die Datei wird nicht überschrieben. Bitte einen anderen Entity-Namen wählen.",
            "Den Typ Shop.Auftrag gibt es im Arbeits-Klon schon.",
        ], plan.Problems);
    }
}
