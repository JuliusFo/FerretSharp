using FerretSharp.SampleModel.Data.Converters;
using FerretSharp.SampleModel.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FerretSharp.SampleModel.Data.Configurations;

public sealed class AuftragConfiguration : IEntityTypeConfiguration<Auftrag>
{
    public void Configure(EntityTypeBuilder<Auftrag> builder)
    {
        builder.ToTable("Auftrag");
        builder.Property(a => a.Status).HasConversion(new UpperCaseEnumConverter<AuftragStatus>()).HasMaxLength(20);
        builder.Property(a => a.Notiz).HasColumnType("CLOB");
        builder.HasOne(a => a.Kunde).WithMany(k => k.Auftraege).HasForeignKey(a => a.KundeId);
    }
}

public sealed class AuftragPositionConfiguration : IEntityTypeConfiguration<AuftragPosition>
{
    public void Configure(EntityTypeBuilder<AuftragPosition> builder)
    {
        builder.ToTable("AuftragPosition");
        builder.HasKey(p => new { p.AuftragId, p.PosNr });
        builder.HasOne(p => p.Auftrag).WithMany(a => a.Positionen).HasForeignKey(p => p.AuftragId);
    }
}
