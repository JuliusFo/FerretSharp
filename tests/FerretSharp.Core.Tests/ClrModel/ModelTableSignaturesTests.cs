using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Schema;
using NSubstitute;

namespace FerretSharp.Core.Tests.ClrModel;

/// <summary>
/// After a build the whole model is loaded again; the views of a table follow only if the table reads differently
/// (ADR 0016). A table missed here would show the old model, so every kind of change must count.
/// </summary>
public sealed class ModelTableSignaturesTests
{
    private const string Owner = "APP";
    private static readonly TableRef Kunden = new(Owner, "KUNDEN");
    private static readonly TableRef Auftrag = new(Owner, "AUFTRAG");

    private SchemaCache _schema = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PropertyExport Prop(string name, string column, string type = "int", IReadOnlyList<ValueMapping>? values = null) =>
        new(name, type, type, false, false, column, null, null, null, false, values);

    private static EntityExport Entity(string clrType, string table, IReadOnlyList<PropertyExport> properties, IReadOnlyList<ForeignKeyExport>? keys = null) =>
        new(clrType, clrType, false, null, table, null, null, null, properties, [], keys ?? []);

    private static readonly PropertyExport[] KundeProps = [Prop("KundeId", "KUNDE_ID"), Prop("Name", "NAME", "string")];
    private static readonly PropertyExport[] AuftragProps = [Prop("Id", "ID"), Prop("KundeId", "KUNDE_ID"), Prop("Status", "STATUS", "Status", [new("Offen", "0", "O")])];

    private async Task<ModelTableSignatures> SignaturesAsync(params EntityExport[] entities)
    {
        if (_schema is null)
        {
            var reader = Substitute.For<ISchemaReader>();
            reader.GetTablesAsync(Owner, Arg.Any<CancellationToken>()).Returns([new TableSummary(Owner, "KUNDEN", TableKind.Table), new TableSummary(Owner, "AUFTRAG", TableKind.Table)]);
            reader.GetSynonymTargetsAsync(Owner, Arg.Any<CancellationToken>()).Returns(SynonymTargets.None);
            reader.GetForeignKeysAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns([]);
            static IReadOnlyList<ColumnInfo> Columns(params string[] names) =>
                names.Select((name, i) => new ColumnInfo(name, "NUMBER", null, false, 10, 0, false, false, null, i + 1)).ToList();
            reader.GetColumnsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new Dictionary<string, IReadOnlyList<ColumnInfo>>
            {
                ["KUNDEN"] = Columns("KUNDE_ID", "NAME", "EMAIL"),
                ["AUFTRAG"] = Columns("ID", "KUNDE_ID", "STATUS"),
            });
            _schema = new SchemaCache(reader, Owner);
            await _schema.LoadAsync(Ct);
        }

        var mapping = await ClrModelMapping.BuildAsync(new ModelExport(1, "8.0.0", "Shop.Ctx", "options", null, entities), _schema, Ct);
        return ModelTableSignatures.Of(mapping);
    }

    private Task<ModelTableSignatures> BaseAsync(PropertyExport[]? kunde = null, PropertyExport[]? auftrag = null, string kundeType = "Shop.Kunde",
        IReadOnlyList<ForeignKeyExport>? keys = null) =>
        SignaturesAsync(Entity(kundeType, "KUNDEN", kunde ?? KundeProps), Entity("Shop.Auftrag", "AUFTRAG", auftrag ?? AuftragProps, keys));

    [Fact]
    public async Task The_same_model_loaded_again_changes_no_table()
    {
        var before = await BaseAsync();
        var after = await BaseAsync();

        Assert.Empty(before.ChangedTables(after));
        Assert.True(before.SameEntityNames(after));
    }

    [Fact]
    public async Task A_new_property_changes_only_its_table()
    {
        var before = await BaseAsync();
        var after = await BaseAsync(kunde: [.. KundeProps, Prop("Email", "EMAIL", "string")]);

        Assert.Equal([Kunden], before.ChangedTables(after));
        Assert.True(before.SameEntityNames(after));
    }

    [Fact]
    public async Task Changed_enum_values_and_relationships_change_the_table()
    {
        var before = await BaseAsync();
        var values = await BaseAsync(auftrag: [AuftragProps[0], AuftragProps[1], Prop("Status", "STATUS", "Status", [new("Offen", "0", "O"), new("Zu", "1", "Z")])]);
        var keys = await BaseAsync(keys: [new ForeignKeyExport(["KundeId"], "Shop.Kunde", ["KundeId"], "Kunde", "Auftraege", false)]);

        Assert.Equal([Auftrag], before.ChangedTables(values));
        Assert.Equal([Auftrag], before.ChangedTables(keys));
    }

    [Fact]
    public async Task A_renamed_entity_changes_its_table_and_the_names()
    {
        var before = await BaseAsync();
        var after = await BaseAsync(kundeType: "Shop.Customer");

        Assert.Equal([Kunden], before.ChangedTables(after));
        Assert.False(before.SameEntityNames(after));
    }

    [Fact]
    public async Task Loading_or_unlinking_a_model_changes_every_mapped_table()
    {
        var model = await BaseAsync();

        Assert.Equal([Kunden, Auftrag], ModelTableSignatures.Empty.ChangedTables(model).OrderByDescending(t => t.Name));
        Assert.Equal([Kunden, Auftrag], model.ChangedTables(ModelTableSignatures.Empty).OrderByDescending(t => t.Name));
        Assert.False(model.SameEntityNames(ModelTableSignatures.Empty));
    }
}
