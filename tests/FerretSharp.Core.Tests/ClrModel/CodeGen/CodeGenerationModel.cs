using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Data;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;
using FerretSharp.Core.Settings;
using NSubstitute;

namespace FerretSharp.Core.Tests.ClrModel;

/// <summary>A KUNDEN table like the sample model's, with the cases WP-15 has to handle.</summary>
internal static class CodeGenerationModel
{
    public const string Owner = "APP";

    public static readonly TableDetails Kunden = new(new TableSummary(Owner, "KUNDEN", TableKind.Table),
        [
            new("KUNDE_ID", "NUMBER", null, false, 10, 0, false, false, null, 1),
            new("NAME", "VARCHAR2", 100, true, null, null, false, false, null, 2),
            new("ERSTELLT_AM", "DATE", null, false, null, null, false, false, null, 3),
            new("UMSATZ", "NUMBER", null, false, 12, 2, true, false, null, 4),
            new("GESPERRT", "CHAR", 1, false, null, null, false, false, null, 5),
            new("KUNDENART", "NUMBER", null, false, 2, 0, false, false, null, 6),
            new("STATUS", "VARCHAR2", 20, true, null, null, true, false, null, 7),
            new("LEGACY_CODE", "VARCHAR2", 10, true, null, null, true, false, null, 8),
            new("MANDANT_ID", "NUMBER", null, false, 5, 0, true, false, null, 9),
            new("EXTERN_ID", "RAW", 16, false, null, null, true, false, null, 10),
            new("GEPRUEFT", "CHAR", 1, false, null, null, true, false, null, 11),
            new("NOTIZ", "CLOB", null, false, null, null, true, false, null, 12),
        ],
        ["KUNDE_ID"], [], false);

    public static readonly TableDetails View = new(new TableSummary(Owner, "V_KUNDEN", TableKind.View),
        [new("KUNDE_ID", "NUMBER", null, false, 10, 0, false, false, null, 1)], [], [], false);

    private static PropertyExport Property(string name, string type, string column, bool nullable = false) =>
        new(name, type, "System." + type, nullable, false, column, null, null, null, false, null);

    public static async Task<ClrModelMapping> MappingAsync(string? dbSet = "Kunden")
    {
        var reader = Substitute.For<ISchemaReader>();
        reader.GetTablesAsync(Owner, Arg.Any<CancellationToken>()).Returns((IReadOnlyList<TableSummary>)[Kunden.Table, View.Table]);
        reader.GetSynonymTargetsAsync(Owner, Arg.Any<CancellationToken>()).Returns(SynonymTargets.None);
        reader.GetForeignKeysAsync(Owner, Arg.Any<CancellationToken>()).Returns((IReadOnlyList<ForeignKeyInfo>)[]);
        reader.GetColumnsAsync(Owner, Arg.Any<CancellationToken>()).Returns((IReadOnlyDictionary<string, IReadOnlyList<ColumnInfo>>)
            new Dictionary<string, IReadOnlyList<ColumnInfo>>
            {
                ["KUNDEN"] = Kunden.Columns,
                ["V_KUNDEN"] = Kunden.Columns.Where(c => c.Name == "KUNDE_ID").ToList(),
            });
        var schema = new SchemaCache(reader, Owner);
        await schema.LoadAsync(TestContext.Current.CancellationToken);

        var kunde = new EntityExport("Shop.Kunde", "Shop.Kunde", false, null, "KUNDEN", null, null, null,
            [
                Property("KundeId", "int", "KUNDE_ID"),
                Property("Name", "string", "NAME"),
                Property("ErstelltAm", "DateTime", "ERSTELLT_AM"),
                Property("Umsatz", "decimal", "UMSATZ", nullable: true),
                new("Gesperrt", "bool", "System.Boolean", false, false, "GESPERRT", null, "JaNeinConverter", "string", false,
                    [new("false", "False", "N"), new("true", "True", "J")]),
                new("Kundenart", "Kundenart", "Shop.Kundenart", false, false, "KUNDENART", null, null, "int", false,
                    [new("Privat", "1", "1"), new("Gewerbe", "2", "2"), new("Behoerde", "3", "3", "Behörde")]),
                new("Status", "KundeStatus", "Shop.KundeStatus", true, false, "STATUS", null, "UpperCaseEnumConverter<KundeStatus>", "string", false,
                    [new("Aktiv", "0", "AKTIV"), new("Inaktiv", "1", "INAKTIV")]),
                Property("MandantId", "int", "MANDANT_ID", nullable: true) with { IsShadow = true },
                Property("ExternId", "Guid", "EXTERN_ID", nullable: true),
                new("Geprueft", "bool", "System.Boolean", true, false, "GEPRUEFT", null, "JaNeinConverter", "string", false,
                    [new("false", "False", "N"), new("true", "True", "J")]),
                Property("Notiz", "string", "NOTIZ", nullable: true),
            ],
            ["KundeId"], [], dbSet);
        var view = new EntityExport("Shop.KundeView", "Shop.KundeView", false, null, null, null, "V_KUNDEN", null,
            [Property("KundeId", "int", "KUNDE_ID")], [], []);
        return await ClrModelMapping.BuildAsync(new ModelExport(1, "8.0.0", "Shop.Ctx", "options", null, [kunde, view]), schema,
            TestContext.Current.CancellationToken);
    }

    public static async Task<TablePresentation> PresentationAsync(string? dbSet = "Kunden") =>
        TablePresentation.Create(Kunden, await MappingAsync(dbSet), ClrNameDisplay.Beside);

    public static int Index(string column) => Kunden.Columns.Select(c => c.Name).ToList().IndexOf(column);
}
