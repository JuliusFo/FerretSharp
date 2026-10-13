using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace FerretSharp.FakeEfModel;

public sealed class Thing
{
    public int Id { get; set; }

    public string Name { get; set; } = "";
}

/// <summary>Nothing listens there: a command that reaches it fails at once instead of waiting.</summary>
internal static class Unreachable
{
    public const string ConnectionString = "Data Source=127.0.0.1:9/NOWHERE;User Id=nobody;Password=none;Connection Timeout=3";
}

/// <summary>Created through its design-time factory, which configures the provider and the connection string.</summary>
public class FactoryContext(DbContextOptions<FactoryContext> options) : DbContext(options)
{
    public DbSet<Thing> Things => Set<Thing>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<Thing>(e =>
        {
            e.ToTable("THINGS");
            e.Property(t => t.Id).HasColumnName("ID");
            e.Property(t => t.Name).HasColumnName("NAME");
        });
}

public sealed class FactoryContextFactory : IDesignTimeDbContextFactory<FactoryContext>
{
    public FactoryContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<FactoryContext>().UseOracle(Unreachable.ConnectionString).Options);
}

/// <summary>Configures itself in <c>OnConfiguring</c>; there is no constructor taking options.</summary>
public class SelfConfiguredContext : DbContext
{
    public DbSet<Thing> Things => Set<Thing>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) => optionsBuilder.UseOracle(Unreachable.ConnectionString);

    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.Entity<Thing>().ToTable("THINGS");
}
