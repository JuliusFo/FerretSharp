# Sample EF Core project

`FerretSharp.SampleModel` stands in for a user's own .NET project when trying out and testing the C# model (WP-11,
ADR 0009). It is written like a real "database first, by hand" project with EF Core 8 and `Oracle.EntityFrameworkCore`:

- `FerretSharp.SampleModel.Entities` – the entity classes (with doc comments), enums and a base class for master data.
- `FerretSharp.SampleModel.Data` – `AppDbContext(DbContextOptions options)`, one `IEntityTypeConfiguration` per entity, a
  generic base configuration, own value converters (bool ↔ `'J'`/`'N'`, enum ↔ upper-case name) and a naming convention
  in code (`KundeId` → `KUNDE_ID`, tables upper case).
- `KundeAuftraegeView` on the view V_KUNDEN_AUFTRAEGE (`ToView` plus key); the convention gives it a table name too, as in the user's project.
- Deliberate drift for the comparison: `Kunde.Email` has no column, `KUNDEN.ANZAHL` no property, `Newsletter` no table.
- `Auftrag.Bearbeiter` → `Mitarbeiter`: a navigation without FK constraint in the database (WP-12, FK navigation "aus C#-Modell").

It maps the sample database (`tools/sample-db`, including `05-clr-model.sql` and `06-clr-relations.sql`). The folder has its own
`Directory.Build.props`/`Directory.Packages.props`, so it inherits nothing from FerretSharp's build.

To try it: build the solution (or `dotnet build samples/FerretSharp.SampleModel.Data`), then in FerretSharp edit the
connection to the sample database and enter
`samples\FerretSharp.SampleModel.Data\FerretSharp.SampleModel.Data.csproj` under “C# model”.
