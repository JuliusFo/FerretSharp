using FerretSharp.SampleModel.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FerretSharp.SampleModel.Data.Configurations;

/// <summary>The columns every master data table shares; derived configurations add their own.</summary>
public abstract class StammdatenConfiguration<T> : IEntityTypeConfiguration<T>
    where T : Stammdaten
{
    public void Configure(EntityTypeBuilder<T> builder)
    {
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property(s => s.Bezeichnung).HasMaxLength(100).IsRequired();
        ConfigureEntity(builder);
    }

    protected virtual void ConfigureEntity(EntityTypeBuilder<T> builder)
    {
    }
}

public sealed class ArtikelConfiguration : StammdatenConfiguration<Artikel>
{
    protected override void ConfigureEntity(EntityTypeBuilder<Artikel> builder)
    {
        builder.HasOne(a => a.Hersteller).WithMany().HasForeignKey(a => a.HerstellerId);
        builder.HasOne(a => a.Kategorie).WithMany().HasForeignKey(a => a.KategorieId);
    }
}

public sealed class HerstellerConfiguration : StammdatenConfiguration<Hersteller>;

public sealed class KategorieConfiguration : StammdatenConfiguration<Kategorie>
{
    protected override void ConfigureEntity(EntityTypeBuilder<Kategorie> builder) => builder.ToTable("Kategorie");
}
