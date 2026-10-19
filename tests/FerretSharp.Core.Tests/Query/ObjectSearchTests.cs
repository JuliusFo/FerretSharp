using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Tests.Query;

public class ObjectSearchTests
{
    private static readonly TableSummary[] Tables =
    [
        new("APP", "AUFTRAG_KUNDE", TableKind.Table),
        new("APP", "KD_STAMM", TableKind.Table),
        new("APP", "KUNDEN", TableKind.Table),
        new("APP", "KUNDEN_ADRESSEN", TableKind.Table),
        new("APP", "V_KUNDEN_OFFEN", TableKind.View),
        new("ALT", "T_KUNDE_ALT", TableKind.Table, new SynonymInfo("APP", "KUNDE_ALT")),
    ];

    private static readonly Dictionary<string, string> Entities = new()
    {
        ["KD_STAMM"] = "KundenStamm",
        ["KUNDEN"] = "Kunde",
    };

    private static IReadOnlyList<string> FindTables(string? query) =>
        ObjectSearch.FindTables(Tables, query, t => Entities.GetValueOrDefault(t.Name)).Select(t => t.Name).ToList();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Nothing_without_a_query(string? query)
    {
        Assert.Empty(FindTables(query));
        Assert.Empty(ObjectSearch.FindPlSql([new PlSqlObjectSummary("APP", "PKG_KUNDE", PlSqlKind.Package, "VALID")], query));
    }

    [Fact]
    public void Exact_then_prefix_then_word_start_list_order_within()
    {
        // KUNDEN is found by its entity name "Kunde" exactly; KD_STAMM's entity name starts with the term.
        Assert.Equal(["KUNDEN", "KD_STAMM", "KUNDEN_ADRESSEN", "T_KUNDE_ALT", "AUFTRAG_KUNDE", "V_KUNDEN_OFFEN"], FindTables("kunde"));
    }

    [Fact]
    public void The_entity_name_finds_a_table_its_name_does_not()
    {
        Assert.Equal(["KD_STAMM"], FindTables("kundenstamm"));
        Assert.Equal(["KD_STAMM"], FindTables("stamm"));
    }

    [Fact]
    public void Every_term_must_occur_in_one_name() =>
        Assert.Equal(["KUNDEN_ADRESSEN"], FindTables("adr kunden"));

    [Fact]
    public void A_synonym_is_found_by_the_name_shown_and_the_real_name()
    {
        Assert.Equal(["T_KUNDE_ALT"], FindTables("kunde_alt"));
        Assert.Equal(["T_KUNDE_ALT"], FindTables("t_kunde"));
    }

    [Fact]
    public void Without_entity_names_only_database_names_count() =>
        Assert.Empty(ObjectSearch.FindTables(Tables, "stamm kunden"));

    [Fact]
    public void PlSql_units_by_name_ranked()
    {
        PlSqlObjectSummary[] units =
        [
            new("APP", "AUFTRAG_KUNDE_PRUEFEN", PlSqlKind.Procedure, "VALID"),
            new("APP", "KUNDE_ANLEGEN", PlSqlKind.Function, "VALID"),
            new("APP", "PKG_KUNDE", PlSqlKind.Package, "VALID"),
            new("APP", "TRG_LAGER", PlSqlKind.Trigger, "VALID"),
        ];

        Assert.Equal(
            ["KUNDE_ANLEGEN", "AUFTRAG_KUNDE_PRUEFEN", "PKG_KUNDE"],
            ObjectSearch.FindPlSql(units, "kunde").Select(u => u.Name));
    }
}
