using FerretSharp.Core.ClrModel;
using FerretSharp.Testing;

namespace FerretSharp.Core.Tests.ClrModel;

/// <summary>Finding an entity's class and configuration in the work clone by syntax (WP-41).</summary>
public sealed class CodeScanTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "work-clone");

    [Fact]
    public void A_configuration_counts_only_for_the_entity_its_file_sees_when_two_types_share_the_name()
    {
        var scan = CodeScan.Of(Root,
        [
            ("Kunde.cs", "namespace Shop.Entities;\npublic class Kunde;\n"),
            ("Dto.cs", "namespace Shop.Api;\npublic class Kunde;\n"),
            ("Mine.cs", "using Shop.Entities;\nclass A : IEntityTypeConfiguration<Kunde> { public void Configure(EntityTypeBuilder<Kunde> b) { } }\n"),
            // B stands in Shop.Api: that namespace's own Kunde comes before the global using, as in C#.
            ("Theirs.cs", "namespace Shop.Api;\nclass B : IEntityTypeConfiguration<Kunde> { public void Configure(EntityTypeBuilder<Kunde> b) { } }\n"),
            ("Qualified.cs", "class C : IEntityTypeConfiguration<Shop.Entities.Kunde> { public void Configure(EntityTypeBuilder<Shop.Entities.Kunde> b) { } }\n"),
            ("Global.cs", "global using Shop.Entities;\n"),
            ("Implicit.cs", "class D : IEntityTypeConfiguration<Kunde> { public void Configure(EntityTypeBuilder<Kunde> b) { } }\n"),
        ]);

        var sites = scan.ConfigurationsOf("Shop.Entities.Kunde");

        Assert.Equal(["A", "C", "D"], sites.Select(s => s.ClassName).Order(StringComparer.Ordinal));
        Assert.Equal(["Kunde.cs"], scan.TypesNamed("Shop.Entities.Kunde").Select(t => t.File.Relative));
    }

    [Fact]
    public void Nested_and_generic_types_are_named_as_the_model_names_them()
    {
        var scan = CodeScan.Of(Root, [("A.cs", "namespace Shop { namespace Inner { class Outer { class Kunde<T> { } } } }")]);

        Assert.Equal(["Shop.Inner.Outer", "Shop.Inner.Outer+Kunde`1"], scan.Types.Select(t => t.FullName));
    }

    [Fact]
    public void Only_files_naming_a_wanted_identifier_are_parsed_and_bin_obj_and_dot_folders_are_skipped()
    {
        using var clone = new TestFolder();
        foreach (var folder in new[] { "src", "src/bin", "src/obj", ".git" })
        {
            Directory.CreateDirectory(clone.Combine(folder.Split('/')));
            File.WriteAllText(clone.Combine([.. folder.Split('/'), "Kunde.cs"]), "class Kunde {}");
        }

        File.WriteAllText(clone.Combine("src", "Other.cs"), "class KundenListe {}"); // the word only as part of another

        var scan = CodeScan.Read(clone.Path, ["Kunde"], TestContext.Current.CancellationToken);

        Assert.Equal([Path.Combine("src", "Kunde.cs")], scan.Files.Select(f => f.Relative));
    }

    /// <summary>Review WP-41, K8: every ANSI file of the clone was named, also those that have nothing to do with the entity.</summary>
    [Fact]
    public void A_file_that_is_no_utf8_is_named_only_if_it_holds_a_wanted_word()
    {
        using var clone = new TestFolder();
        File.WriteAllBytes(clone.Combine("Kunde.cs"), [.. "class Kunde { } // Gr"u8, 0xF6, 0xDF, 0x65]); // "Größe" in Windows-1252
        File.WriteAllBytes(clone.Combine("Other.cs"), [.. "class Other { } // Gr"u8, 0xF6, 0xDF, 0x65]);

        var scan = CodeScan.Read(clone.Path, ["Kunde"], TestContext.Current.CancellationToken);

        Assert.Equal(["Kunde.cs"], scan.Unreadable);
    }
}
