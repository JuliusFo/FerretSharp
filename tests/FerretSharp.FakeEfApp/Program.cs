// A stand-in for a user's app (WP-33): EF Core 8 with lazy-loading proxies and no diagnostics code. The integration tests
// start it, attach to it from outside and compare what they read with the workload below.
//
//   --provider sqlite|oracle   SQLite file (--db <path>) or Oracle (connection string in FAKE_EF_CONNECTION: never on the
//                              command line, a trace reads that)
//   --rounds <n>               rounds of the workload (default 3)
//   anything else              ignored (e.g. -environment=…, which the trace reads)
//
// Protocol on stdin/stdout: prints "ready" once the tables are there, waits for "go", runs the rounds, prints "done", and
// ends at "exit" or when stdin closes.
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

var provider = Option("--provider") ?? "sqlite";
var rounds = int.Parse(Option("--rounds") ?? "3", System.Globalization.CultureInfo.InvariantCulture);
Action<DbContextOptionsBuilder> configure = provider == "oracle"
    ? o => o.UseOracle(Environment.GetEnvironmentVariable("FAKE_EF_CONNECTION"))
    : o => o.UseSqlite($"Data Source={Option("--db") ?? Path.Combine(Path.GetTempPath(), $"fake-ef-{Environment.ProcessId}.db")}");

using (var setup = new ShopContext(configure))
{
    foreach (var table in new[] { "TR_POSITION", "TR_AUFTRAG" })
    {
        try
        {
            setup.Database.ExecuteSqlRaw("DROP TABLE \"" + table + "\"");
        }
        catch (Exception)
        {
            // not there yet
        }
    }

    setup.GetService<IRelationalDatabaseCreator>().CreateTables();
    for (var i = 1; i <= 5; i++)
    {
        var order = new Auftrag { Nummer = $"A-{i}" };
        for (var p = 1; p <= 3; p++)
        {
            order.Positionen.Add(new Position { Artikel = $"Artikel {i}.{p}", Menge = p });
        }

        setup.Auftraege.Add(order);
    }

    setup.SaveChanges();
}

Console.WriteLine("ready");
if (Console.ReadLine() != "go")
{
    return;
}

for (var round = 1; round <= rounds; round++)
{
    using var db = new ShopContext(configure);

    // LINQ, then lazy loading per order (N+1)
    var orders = db.Auftraege.OrderBy(a => a.Id).ToList();
    _ = orders.Sum(o => o.Positionen.Count);

    // FromSql as it is and with LINQ on top, then a raw statement
    _ = db.Positionen.FromSqlRaw("SELECT * FROM \"TR_POSITION\" WHERE \"MENGE\" >= {0}", 3).ToList();
    _ = db.Positionen.FromSqlRaw("SELECT * FROM \"TR_POSITION\" WHERE \"MENGE\" >= {0}", 2).Count();
    db.Database.ExecuteSqlRaw("UPDATE \"TR_POSITION\" SET \"MENGE\" = \"MENGE\" WHERE \"ID\" = {0}", 1);

    // SaveChanges and ExecuteUpdate
    orders[0].Positionen.Add(new Position { Artikel = $"Nachtrag {round}", Menge = 1 });
    db.SaveChanges();
    db.Positionen.Where(p => p.Artikel.StartsWith("Nachtrag")).ExecuteUpdate(s => s.SetProperty(p => p.Menge, p => p.Menge + 1));
}

Console.WriteLine("done");
while (Console.ReadLine() is { } line && line != "exit")
{
}

string? Option(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;

internal sealed class ShopContext(Action<DbContextOptionsBuilder> provider) : DbContext
{
    public DbSet<Auftrag> Auftraege => Set<Auftrag>();

    public DbSet<Position> Positionen => Set<Position>();

    protected override void OnConfiguring(DbContextOptionsBuilder options)
    {
        provider(options);
        options.UseLazyLoadingProxies();
    }

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Auftrag>(e =>
        {
            e.ToTable("TR_AUFTRAG");
            e.Property(a => a.Id).HasColumnName("ID");
            e.Property(a => a.Nummer).HasColumnName("NUMMER").HasMaxLength(20);
        });
        model.Entity<Position>(e =>
        {
            e.ToTable("TR_POSITION");
            e.Property(p => p.Id).HasColumnName("ID");
            e.Property(p => p.Artikel).HasColumnName("ARTIKEL").HasMaxLength(50);
            e.Property(p => p.Menge).HasColumnName("MENGE");
            e.Property(p => p.AuftragId).HasColumnName("AUFTRAG_ID");
        });
    }
}

public class Auftrag
{
    public int Id { get; set; }

    public string Nummer { get; set; } = "";

    public virtual ICollection<Position> Positionen { get; set; } = new List<Position>();
}

public class Position
{
    public int Id { get; set; }

    public string Artikel { get; set; } = "";

    public int Menge { get; set; }

    public int AuftragId { get; set; }

    public virtual Auftrag? Auftrag { get; set; }
}
