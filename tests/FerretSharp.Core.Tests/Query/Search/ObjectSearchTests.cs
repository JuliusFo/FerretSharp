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

    /// <summary>
    /// The entity name finds a table its name does not, every term must occur in one name, and a synonym is found by the name
    /// shown and the real name (review 3.70.0, C17: three tests of one call).
    /// </summary>
    [Theory]
    [InlineData("kundenstamm", "KD_STAMM")]
    [InlineData("stamm", "KD_STAMM")]
    [InlineData("adr kunden", "KUNDEN_ADRESSEN")]
    [InlineData("kunde_alt", "T_KUNDE_ALT")]
    [InlineData("t_kunde", "T_KUNDE_ALT")]
    public void One_table_is_found_by_its_entity_name_every_term_or_a_synonym_s_names(string terms, string table) =>
        Assert.Equal([table], FindTables(terms));

    [Fact]
    public void Without_entity_names_only_database_names_count() =>
        Assert.Empty(ObjectSearch.FindTables(Tables, "stamm kunden"));

    /// <summary>Review of WP-38: the public ranking does not fail without terms.</summary>
    [Fact]
    public void Without_terms_every_name_matches_alike()
    {
        Assert.Equal(NameMatch.Contains, NameRank.Of("KUNDEN", []));
        Assert.Equal(NameMatch.Contains, NameRank.OfClrName("Kunde", []));
    }

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
