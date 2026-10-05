using FerretSharp.SampleModel.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FerretSharp.SampleModel.Data.Configurations;

/// <summary>An entity on a view, configured like in the user's project: ToView plus a key, no ToTable.</summary>
public sealed class KundeAuftraegeViewConfiguration : IEntityTypeConfiguration<KundeAuftraegeView>
{
    public void Configure(EntityTypeBuilder<KundeAuftraegeView> builder) =>
        builder.ToView("V_KUNDEN_AUFTRAEGE").HasKey(v => v.KundeId);
}
