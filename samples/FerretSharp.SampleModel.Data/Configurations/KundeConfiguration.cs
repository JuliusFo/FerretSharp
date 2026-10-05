using FerretSharp.SampleModel.Data.Converters;
using FerretSharp.SampleModel.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FerretSharp.SampleModel.Data.Configurations;

public sealed class KundeConfiguration : IEntityTypeConfiguration<Kunde>
{
    public void Configure(EntityTypeBuilder<Kunde> builder)
    {
        builder.Property(k => k.KundeId).ValueGeneratedNever();
        builder.Property(k => k.Name).HasMaxLength(100).IsRequired();
        builder.Property(k => k.Kuerzel).HasMaxLength(3).IsFixedLength();
        builder.Property(k => k.Umsatz).HasPrecision(12, 2);
        builder.Property(k => k.Gesperrt).HasConversion(new JaNeinConverter()).HasColumnType("CHAR(1)");
    }
}
