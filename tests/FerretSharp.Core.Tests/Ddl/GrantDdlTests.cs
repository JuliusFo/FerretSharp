using FerretSharp.Core.Ddl;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Tests.Ddl;

/// <summary>
/// WP-71: GRANT and REVOKE name the owner and the grantee as in the dictionary, PUBLIC as the keyword, columns only for the
/// privileges Oracle grants per column – and pass the schema path, REVOKE as well as GRANT.
/// </summary>
public sealed class GrantDdlTests
{
    private static readonly TableRef Target = new("APP", "Kunde Alt");

    private static ObjectGrant Grant(string grantee, string privilege, string[]? columns = null, bool grantable = false) =>
        new(grantee, privilege, columns ?? [], "APP", grantable, false);

    [Fact]
    public void Columns_go_to_the_privileges_Oracle_grants_per_column()
    {
        var step = GrantDdl.Grant(Target, ["SELECT", "UPDATE", "REFERENCES"], ["NAME", "Notiz"], "U1", grantOption: true);

        Assert.Equal("GRANT SELECT, UPDATE (\"NAME\", \"Notiz\"), REFERENCES (\"NAME\", \"Notiz\") ON \"APP\".\"Kunde Alt\" TO \"U1\" WITH GRANT OPTION", step.Sql);
        Assert.Equal(DdlStepKind.Grant, step.Kind);
        Assert.Null(SqlScript.DdlRejection(step.Sql));
    }

    [Fact]
    public void PUBLIC_is_the_keyword_not_a_quoted_name()
    {
        Assert.Equal("GRANT SELECT ON \"APP\".\"Kunde Alt\" TO PUBLIC", GrantDdl.Grant(Target, ["SELECT"], [], "PUBLIC", grantOption: false).Sql);
        Assert.Equal("REVOKE SELECT ON \"APP\".\"Kunde Alt\" FROM PUBLIC", GrantDdl.Revoke(Target, Grant("PUBLIC", "SELECT")).Sql);
    }

    [Fact]
    public void A_revoke_is_per_object_also_for_a_grant_on_columns_and_passes_the_schema_path()
    {
        var sql = GrantDdl.Revoke(Target, Grant("u2", "UPDATE", ["NAME"])).Sql;

        Assert.Equal("REVOKE UPDATE ON \"APP\".\"Kunde Alt\" FROM \"u2\"", sql);
        Assert.Null(SqlScript.DdlRejection(sql));
    }

    [Theory]
    [InlineData("SELECT; DROP TABLE x")]
    [InlineData("select")]
    [InlineData("MERGE  VIEW")]
    [InlineData("")]
    public void Anything_but_a_privilege_keyword_is_refused(string privilege)
    {
        Assert.Throws<ArgumentException>(() => GrantDdl.Grant(Target, [privilege], [], "U1", grantOption: false));
        Assert.Throws<ArgumentException>(() => GrantDdl.Revoke(Target, Grant("U1", privilege)));
    }

    [Fact]
    public void The_script_gives_each_grant_again_PUBLIC_first_and_per_grantee_options_and_columns()
    {
        IReadOnlyList<ObjectGrant> grants =
        [
            Grant("U1", "SELECT"),
            Grant("U1", "INSERT"),
            Grant("U1", "UPDATE", ["NAME", "NOTE"], grantable: true),
            Grant("U1", "REFERENCES", ["ID"]),
            Grant("PUBLIC", "SELECT"),
            Grant("U1", "DELETE", grantable: true),
            new("U2", "SELECT", [], "APP", Grantable: true, Hierarchy: true),
        ];

        Assert.Equal(
        [
            "GRANT SELECT ON \"APP\".\"Kunde Alt\" TO PUBLIC",
            "GRANT INSERT, SELECT ON \"APP\".\"Kunde Alt\" TO \"U1\"",
            "GRANT DELETE ON \"APP\".\"Kunde Alt\" TO \"U1\" WITH GRANT OPTION",
            "GRANT REFERENCES (\"ID\") ON \"APP\".\"Kunde Alt\" TO \"U1\"",
            "GRANT UPDATE (\"NAME\", \"NOTE\") ON \"APP\".\"Kunde Alt\" TO \"U1\" WITH GRANT OPTION",
            // review of WP-71: the other order is ORA-03048
            "GRANT SELECT ON \"APP\".\"Kunde Alt\" TO \"U2\" WITH HIERARCHY OPTION WITH GRANT OPTION",
        ], GrantDdl.Script(Target, grants));
    }

    [Fact]
    public void A_grantee_reaches_an_object_directly_or_through_PUBLIC_but_not_through_a_role()
    {
        var direct = new SchemaObjectGrants(Target, GrantObjectKind.Table, [Grant("U1", "SELECT")]);
        var overPublic = direct with { Grants = [Grant("PUBLIC", "SELECT")] };
        var overRole = direct with { Grants = [Grant("APP_ROLE", "SELECT")] };

        Assert.True(direct.Reaches("U1"));
        Assert.True(overPublic.Reaches("U1"));
        Assert.False(overRole.Reaches("U1"));
        Assert.False((direct with { Grants = [] }).Reaches("U1"));
    }
}
