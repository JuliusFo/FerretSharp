using FerretSharp.Core.Ddl;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Tests.Ddl;

/// <summary>
/// WP-68: synonyms are created and dropped with their owner (a schema in the profile does not set the session's schema),
/// never with OR REPLACE, and through the schema path – which takes all four forms.
/// </summary>
public sealed class SynonymDdlTests
{
    private static readonly TableRef Target = new("OTHER", "Kunde Alt");

    [Theory]
    [InlineData("APP", "KUNDE", "CREATE SYNONYM \"APP\".\"KUNDE\" FOR \"OTHER\".\"Kunde Alt\"", "DROP SYNONYM \"APP\".\"KUNDE\"")]
    [InlineData("PUBLIC", "Kunde", "CREATE PUBLIC SYNONYM \"Kunde\" FOR \"OTHER\".\"Kunde Alt\"", "DROP PUBLIC SYNONYM \"Kunde\"")]
    public void Statements_name_the_owner_or_PUBLIC_and_pass_the_schema_path(string owner, string name, string create, string drop)
    {
        var synonym = new SynonymInfo(owner, name);

        Assert.Equal(create, SynonymDdl.Create(synonym, Target).Sql);
        Assert.Equal(drop, SynonymDdl.Drop(synonym).Sql);
        Assert.Null(SqlScript.DdlRejection(create));
        Assert.Null(SqlScript.DdlRejection(drop));
    }

    [Theory]
    [InlineData("APP", true, "CREATE SYNONYM")]
    [InlineData("APP", false, null)]
    [InlineData("OTHER", true, "CREATE ANY SYNONYM")]
    [InlineData("OTHER", false, "DROP ANY SYNONYM")]
    [InlineData("PUBLIC", true, "CREATE PUBLIC SYNONYM")]
    [InlineData("PUBLIC", false, "DROP PUBLIC SYNONYM")]
    public void The_privilege_named_after_ORA_01031_depends_on_whose_synonym_it_is(string owner, bool create, string? privilege) =>
        Assert.Equal(privilege, SynonymDdl.RequiredPrivilege(new SynonymInfo(owner, "S"), create, loginUser: "APP"));

    [Theory]
    // over a database link nothing is checked
    [InlineData(true, false, false, true, false, SynonymTargetState.DbLink)]
    [InlineData(false, true, true, true, false, SynonymTargetState.Ok)] // recreated after a drop: INVALID until used
    [InlineData(false, false, true, true, false, SynonymTargetState.Missing)] // dropped
    [InlineData(false, false, false, false, false, SynonymTargetState.Missing)] // the owner is gone
    [InlineData(false, false, false, true, true, SynonymTargetState.Missing)] // the own schema hides nothing
    [InlineData(false, false, false, true, false, SynonymTargetState.MissingOrNoAccess)]
    public void A_target_not_visible_is_missing_only_where_the_dictionary_can_tell(
        bool dbLink, bool visible, bool invalid, bool ownerExists, bool ownSchema, SynonymTargetState expected) =>
        Assert.Equal(expected, SchemaSynonym.StateOf(dbLink, visible, invalid, ownerExists, ownSchema));
}
