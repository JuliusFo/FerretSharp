using FerretSharp.SampleModel.Entities;
using Microsoft.EntityFrameworkCore;

namespace FerretSharp.SampleModel.Data;

/// <summary>
/// The sample context, written like a real DB-first-by-hand project: configurations per entity, a generic base
/// configuration, own value converters and a naming convention applied in code (see <see cref="OracleNamingConvention"/>).
/// </summary>
public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions options)
        : base(options)
    {
    }

    public DbSet<Kunde> Kunden => Set<Kunde>();

    public DbSet<Auftrag> Auftraege => Set<Auftrag>();

    public DbSet<AuftragPosition> AuftragPositionen => Set<AuftragPosition>();

    public DbSet<Artikel> Artikel => Set<Artikel>();

    public DbSet<Hersteller> Hersteller => Set<Hersteller>();

    public DbSet<Kategorie> Kategorien => Set<Kategorie>();

    public DbSet<Mitarbeiter> Mitarbeiter => Set<Mitarbeiter>();

    public DbSet<Newsletter> Newsletter => Set<Newsletter>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
        OracleNamingConvention.Apply(modelBuilder);
    }
}
