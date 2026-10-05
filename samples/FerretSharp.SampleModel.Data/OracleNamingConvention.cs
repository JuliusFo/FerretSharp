using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace FerretSharp.SampleModel.Data;

/// <summary>
/// Table and column names as in the database: upper case, a word boundary becomes an underscore
/// (<c>KundeId</c> → <c>KUNDE_ID</c>, <c>AuftragPosition</c> → <c>AUFTRAG_POSITION</c>). Code, not configuration –
/// the reason FerretSharp reads the model from the compiled context (ADR 0009).
/// </summary>
public static partial class OracleNamingConvention
{
    public static void Apply(ModelBuilder modelBuilder)
    {
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            // Every entity gets an upper-case table name – also those mapped to a view (as in the user's project): EF then
            // queries the view and would use the table only for SaveChanges.
            entity.SetTableName(ToOracleName(entity.GetTableName() ?? entity.ClrType.Name));

            foreach (var property in entity.GetProperties())
            {
                property.SetColumnName(ToOracleName(property.GetColumnName()));
            }
        }
    }

    public static string ToOracleName(string name) => WordBoundary().Replace(name, "_$1").ToUpperInvariant();

    [GeneratedRegex("(?<=[a-z0-9])([A-Z])")]
    private static partial Regex WordBoundary();
}
