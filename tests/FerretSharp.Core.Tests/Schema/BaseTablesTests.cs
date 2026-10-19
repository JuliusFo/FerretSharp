using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Tests.Schema;

/// <summary>
/// Base and extension tables: FAHRZEUG is the vehicle, PKW (a car) and LKW (a truck) extend it over FAHRZEUG_ID; WARTUNG
/// references the vehicle, FAHRZEUG has a towing vehicle, LEASING references FAHRZEUG's other unique key NR.
/// </summary>
public class BaseTablesTests
{
    private static readonly TableRef Fahrzeug = new("APP", "FAHRZEUG");
    private static readonly TableRef Pkw = new("APP", "PKW");
    private static readonly TableRef Lkw = new("APP", "LKW");
    private static readonly TableRef Wartung = new("APP", "WARTUNG");
    private static readonly TableRef Leasing = new("APP", "LEASING");
    private static readonly TableRef Zulassung = new("APP", "PKW_ZULASSUNG");
    private static readonly TableRef Elektro = new("APP", "PKW_ELEKTRO");

    private static readonly ForeignKeyInfo PkwFahrzeug = new("FK_PKW_FAHRZEUG", Pkw, ["FAHRZEUG_ID"], Fahrzeug, ["FAHRZEUG_ID"], FkSource.Declared);
    private static readonly ForeignKeyInfo LkwFahrzeug = new("FK_LKW_FAHRZEUG", Lkw, ["FAHRZEUG_ID"], Fahrzeug, ["FAHRZEUG_ID"], FkSource.ClrModel);
    private static readonly ForeignKeyInfo WartungFahrzeug = new("FK_WARTUNG_FAHRZEUG", Wartung, ["FAHRZEUG_ID"], Fahrzeug, ["FAHRZEUG_ID"], FkSource.Declared);
    private static readonly ForeignKeyInfo FahrzeugZug = new("FK_FAHRZEUG_ZUG", Fahrzeug, ["ZUG_ID"], Fahrzeug, ["FAHRZEUG_ID"], FkSource.Declared);
    private static readonly ForeignKeyInfo LeasingFahrzeug = new("FK_LEASING_FAHRZEUG", Leasing, ["FAHRZEUG_NR"], Fahrzeug, ["NR"], FkSource.Declared);
    private static readonly ForeignKeyInfo ZulassungPkw = new("FK_ZULASSUNG_PKW", Zulassung, ["FAHRZEUG_ID"], Pkw, ["FAHRZEUG_ID"], FkSource.Declared);

    private static readonly Dictionary<TableRef, IReadOnlyList<string>> PrimaryKeys = new()
    {
        [Fahrzeug] = ["FAHRZEUG_ID"], [Pkw] = ["FAHRZEUG_ID"], [Lkw] = ["FAHRZEUG_ID"], [Wartung] = ["WARTUNG_ID"], [Leasing] = ["LEASING_ID"],
        [Zulassung] = ["ZULASSUNG_ID"], [Elektro] = ["PKW_ID"],
    };

    private static IReadOnlyList<InheritedReference> ReferencesOf(TableRef table, params ForeignKeyInfo[] foreignKeys) =>
        BaseTables.ReferencesOf(table, t => PrimaryKeys.GetValueOrDefault(t) ?? [],
            t => foreignKeys.Where(f => f.From == t), t => foreignKeys.Where(f => f.To == t));

    private static readonly ForeignKeyInfo[] Vehicles = [PkwFahrzeug, LkwFahrzeug, WartungFahrzeug, FahrzeugZug, LeasingFahrzeug, ZulassungPkw];

    [Fact]
    public void Extension_table_is_reached_by_what_references_its_base_table()
    {
        var references = ReferencesOf(Pkw, Vehicles);

        Assert.Equal([WartungFahrzeug, FahrzeugZug], references.Select(r => r.ForeignKey));
        Assert.All(references, r => Assert.Equal(Fahrzeug, r.Via));
        Assert.All(references, r => Assert.Equal(["FAHRZEUG_ID"], r.Columns));
    }

    [Fact]
    public void Sibling_extension_tables_and_the_table_itself_are_left_out()
    {
        var references = ReferencesOf(Lkw, Vehicles);

        Assert.DoesNotContain(references, r => r.ForeignKey.From == Pkw || r.ForeignKey.From == Lkw);
        Assert.Equal([WartungFahrzeug, FahrzeugZug], references.Select(r => r.ForeignKey));
    }

    [Fact]
    public void Reference_to_another_key_of_the_base_table_is_left_out() =>
        Assert.DoesNotContain(ReferencesOf(Pkw, Vehicles), r => r.ForeignKey == LeasingFahrzeug);

    [Fact]
    public void Base_table_and_table_without_primary_key_have_no_inherited_references()
    {
        var view = new TableRef("APP", "V_PKW");

        Assert.Empty(ReferencesOf(Fahrzeug, Vehicles));
        Assert.Empty(ReferencesOf(view, [new ForeignKeyInfo("V", view, ["FAHRZEUG_ID"], Fahrzeug, ["FAHRZEUG_ID"], FkSource.Manual), .. Vehicles]));
    }

    [Fact]
    public void Relationship_over_part_of_a_composite_primary_key_is_no_extension()
    {
        Assert.False(BaseTables.IsExtension(new ForeignKeyInfo("FK", Wartung, ["FAHRZEUG_ID"], Fahrzeug, ["FAHRZEUG_ID"], FkSource.Declared), ["FAHRZEUG_ID", "POS"]));
        Assert.True(BaseTables.IsExtension(new ForeignKeyInfo("FK", Wartung, ["POS", "FAHRZEUG_ID"], Fahrzeug, ["A", "B"], FkSource.Declared), ["FAHRZEUG_ID", "POS"]));
        Assert.False(BaseTables.IsExtension(FahrzeugZug, ["FAHRZEUG_ID"]));
    }

    [Fact]
    public void Several_levels_map_the_key_through_renamed_columns_nearest_base_first()
    {
        // PKW_ELEKTRO extends PKW over its own column name; PKW_ZULASSUNG references PKW, WARTUNG references FAHRZEUG.
        var elektroPkw = new ForeignKeyInfo("FK_ELEKTRO_PKW", Elektro, ["PKW_ID"], Pkw, ["FAHRZEUG_ID"], FkSource.Declared);

        var references = ReferencesOf(Elektro, [elektroPkw, .. Vehicles]);

        Assert.Equal([ZulassungPkw, WartungFahrzeug, FahrzeugZug], references.Select(r => r.ForeignKey));
        Assert.Equal([Pkw, Fahrzeug, Fahrzeug], references.Select(r => r.Via));
        Assert.All(references, r => Assert.Equal(["PKW_ID"], r.Columns));
    }

    [Fact]
    public void Composite_key_is_mapped_column_by_column()
    {
        var head = new TableRef("APP", "HEAD");
        var ext = new TableRef("APP", "HEAD_EXT");
        var child = new TableRef("APP", "CHILD");
        // The extension and the referencing table list the key columns in other orders than the extension's key.
        var extHead = new ForeignKeyInfo("FK_EXT", ext, ["E_NR", "E_MANDANT"], head, ["NR", "MANDANT"], FkSource.Declared);
        var childHead = new ForeignKeyInfo("FK_CHILD", child, ["C_NR", "C_MANDANT"], head, ["NR", "MANDANT"], FkSource.Declared);
        var keys = new Dictionary<TableRef, IReadOnlyList<string>> { [head] = ["MANDANT", "NR"], [ext] = ["E_MANDANT", "E_NR"], [child] = ["C_ID"] };
        ForeignKeyInfo[] all = [extHead, childHead];

        var reference = Assert.Single(BaseTables.ReferencesOf(ext, t => keys.GetValueOrDefault(t) ?? [],
            t => all.Where(f => f.From == t), t => all.Where(f => f.To == t)));

        Assert.Equal(["E_NR", "E_MANDANT"], reference.Columns);
        Assert.Equal(ext, reference.Table);
    }

    [Fact]
    public void Reference_reaching_the_same_rows_as_a_direct_one_is_left_out()
    {
        // The C# model also knows WARTUNG -> PKW over the same column: the rows are those of WARTUNG -> FAHRZEUG.
        var wartungPkw = new ForeignKeyInfo("WartungPkw", Wartung, ["FAHRZEUG_ID"], Pkw, ["FAHRZEUG_ID"], FkSource.ClrModel);

        var references = ReferencesOf(Pkw, [wartungPkw, .. Vehicles]);

        Assert.Equal([FahrzeugZug], references.Select(r => r.ForeignKey));
        Assert.Equal([WartungFahrzeug, FahrzeugZug], ReferencesOf(Lkw, [wartungPkw, .. Vehicles]).Select(r => r.ForeignKey));
    }

    [Fact]
    public void Extension_over_another_unique_key_of_the_base_ends_the_way_up()
    {
        // PKW extends FAHRZEUG over FAHRZEUG.NR (unique), not its primary key: only references to NR are reached, and
        // FAHRZEUG's own base is not, since the row does not hold FAHRZEUG_ID.
        var pkwNr = new ForeignKeyInfo("FK_PKW_NR", Pkw, ["FAHRZEUG_ID"], Fahrzeug, ["NR"], FkSource.Declared);
        var flotte = new TableRef("APP", "FLOTTE");
        var fahrzeugFlotte = new ForeignKeyInfo("FK_FAHRZEUG_FLOTTE", Fahrzeug, ["FAHRZEUG_ID"], flotte, ["FAHRZEUG_ID"], FkSource.Declared);
        var keys = new Dictionary<TableRef, IReadOnlyList<string>>(PrimaryKeys) { [flotte] = ["FAHRZEUG_ID"] };
        ForeignKeyInfo[] all = [pkwNr, fahrzeugFlotte, WartungFahrzeug, LeasingFahrzeug, new("FK_X", new TableRef("APP", "X"), ["F_ID"], flotte, ["FAHRZEUG_ID"], FkSource.Declared)];

        var references = BaseTables.ReferencesOf(Pkw, t => keys.GetValueOrDefault(t) ?? [],
            t => all.Where(f => f.From == t), t => all.Where(f => f.To == t));

        var reference = Assert.Single(references);
        Assert.Equal((LeasingFahrzeug, Fahrzeug), (reference.ForeignKey, reference.Via));
        Assert.Equal(["FAHRZEUG_ID"], reference.Columns);
    }

    [Fact]
    public void Diamond_reaches_each_base_table_once()
    {
        // PKW_ELEKTRO extends PKW and FAHRZEUG directly; PKW extends FAHRZEUG.
        var elektroPkw = new ForeignKeyInfo("FK_ELEKTRO_PKW", Elektro, ["PKW_ID"], Pkw, ["FAHRZEUG_ID"], FkSource.Declared);
        var elektroFahrzeug = new ForeignKeyInfo("FK_ELEKTRO_FAHRZEUG", Elektro, ["PKW_ID"], Fahrzeug, ["FAHRZEUG_ID"], FkSource.Declared);

        var references = ReferencesOf(Elektro, [elektroPkw, elektroFahrzeug, .. Vehicles]);

        Assert.Equal([WartungFahrzeug, FahrzeugZug, ZulassungPkw], references.Select(r => r.ForeignKey));
        Assert.Equal([Fahrzeug, Fahrzeug, Pkw], references.Select(r => r.Via));
    }

    [Fact]
    public void Tables_extending_each_other_end_the_way_up()
    {
        var a = new TableRef("APP", "A");
        var b = new TableRef("APP", "B");
        var ab = new ForeignKeyInfo("FK_AB", a, ["ID"], b, ["ID"], FkSource.Declared);
        var ba = new ForeignKeyInfo("FK_BA", b, ["ID"], a, ["ID"], FkSource.Declared);
        var c = new ForeignKeyInfo("FK_CB", new TableRef("APP", "C"), ["B_ID"], b, ["ID"], FkSource.Declared);
        ForeignKeyInfo[] all = [ab, ba, c];

        var references = BaseTables.ReferencesOf(a, t => t == a || t == b ? ["ID"] : ["C_ID"],
            t => all.Where(f => f.From == t), t => all.Where(f => f.To == t));

        Assert.Equal([c], references.Select(r => r.ForeignKey));
    }
}
