using FerretSharp.Core.Connections;
using FerretSharp.Core.Ddl;
using FerretSharp.Core.Oracle;
using FerretSharp.Core.Schema;

namespace FerretSharp.Integration.Tests;

/// <summary>WP-71, measured against Oracle 23: object privileges read, granted and revoked through the schema path.</summary>
public class GrantTests(OracleContainerFixture oracle) : IAsyncLifetime
{
    private const string Grantee = GrantSchema.Grantee;
    private const string Scratch = GrantSchema.Scratch;
    private const string Other = GrantSchema.OtherOwner;

    private OracleSession? _session;
    private string _owner = "";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private OracleSchemaReader Reader => new(_session!);

    private TableRef Own(string name) => new(_owner, name);

    public async ValueTask InitializeAsync()
    {
        var (profile, password) = oracle.RequireProfile();
        var connectionString = oracle.RequireConnectionString();
        await SampleSchema.EnsureCreatedAsync(connectionString, Ct);
        await GrantSchema.EnsureCreatedAsync(connectionString, Ct);
        Assert.SkipUnless(GrantSchema.Available, GrantSchema.UnavailableReason ?? "Grant schema not created.");

        _owner = profile.EffectiveSchema;
        _session = await OracleSession.OpenAsync(
            OracleConnectionStringFactory.Create(profile, password), new SessionContext("FerretSharp", "Grant tests"), Ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_session is not null)
        {
            await _session.DisposeAsync();
        }
    }

    [Fact]
    public async Task An_own_object_shows_every_grant_with_the_column_grants_of_one_privilege_as_one()
    {
        var grants = await Reader.GetGrantsAsync(Own("GR_TABLE"), Ct);

        Assert.Contains(new ObjectGrant(Grantee, "SELECT", [], _owner, false, false), grants);
        Assert.Contains(new ObjectGrant(Grantee, "INSERT", [], _owner, false, false), grants);
        Assert.Contains(new ObjectGrant(Grantee, "UPDATE", ["NAME", "NOTE"], _owner, true, false), grants);
        Assert.Contains(new ObjectGrant(Grantee, "REFERENCES", ["ID"], _owner, false, false), grants);
        Assert.Contains(new ObjectGrant("PUBLIC", "SELECT", [], _owner, false, false), grants);
    }

    /// <summary>On another schema's object only the grants the session takes part in: its own, its role's, those it gave.</summary>
    [Fact]
    public async Task Another_schema_s_object_shows_only_the_grants_the_session_takes_part_in()
    {
        var grants = await Reader.GetGrantsAsync(new TableRef(Other, "T_SHARED"), Ct);

        (string, string)[] expected = [(_owner, "SELECT"), (GrantSchema.Role, "INSERT")];
        Assert.Equal(expected.Order(), grants.Select(g => (g.Grantee, g.Privilege)).Order());
    }

    [Fact]
    public async Task The_schema_list_has_every_grantable_object_also_without_grants_by_kind()
    {
        var objects = (await Reader.GetSchemaGrantsAsync(_owner, Ct)).ToDictionary(o => o.Object.Name);

        Assert.Equal(GrantObjectKind.Table, objects["GR_TABLE"].Kind);
        Assert.Equal(GrantObjectKind.Sequence, objects["GR_SEQ"].Kind);
        Assert.Equal(GrantObjectKind.View, objects["GR_VIEW"].Kind);
        Assert.Equal((GrantObjectKind.Procedure, 0), (objects["GR_PROC"].Kind, objects["GR_PROC"].Grants.Count));
        Assert.Equal(await Reader.GetGrantsAsync(Own("GR_TABLE"), Ct), objects["GR_TABLE"].Grants);

        // reached directly, through PUBLIC, or not at all – a role does not count
        Assert.True(objects["GR_SEQ"].Reaches(Grantee));
        Assert.True(objects["GR_TABLE"].Reaches(Scratch));
        Assert.False(objects["GR_VIEW"].Reaches(Grantee));
        if (SampleSchema.HasMaterializedView)
        {
            Assert.Equal(GrantObjectKind.MaterializedView, objects["MV_UMSATZ"].Kind);
        }
    }

    [Fact]
    public async Task The_users_are_listed_without_roles()
    {
        var users = await Reader.GetUserNamesAsync(Ct);

        Assert.Contains(Grantee, users);
        Assert.DoesNotContain(GrantSchema.Role, users);
    }

    /// <summary>Every privilege offered for a kind of object is one Oracle takes on it (and revokes again).</summary>
    [Fact]
    public async Task Every_offered_privilege_is_granted_and_revoked_through_the_schema_path()
    {
        List<(string Name, GrantObjectKind Kind)> objects =
        [
            ("GR_TABLE", GrantObjectKind.Table), ("GR_VIEW", GrantObjectKind.View), ("GR_SEQ", GrantObjectKind.Sequence),
            ("GR_PROC", GrantObjectKind.Procedure),
        ];
        if (SampleSchema.HasMaterializedView)
        {
            objects.Add(("MV_UMSATZ", GrantObjectKind.MaterializedView));
        }

        foreach (var (name, kind) in objects)
        {
            var privileges = GrantPrivileges.For(kind);
            await _session!.ExecuteDdlAsync(GrantDdl.Grant(Own(name), privileges, [], Scratch, grantOption: false).Sql, Ct);
            var granted = (await Reader.GetGrantsAsync(Own(name), Ct)).Where(g => g.Grantee == Scratch).ToList();
            Assert.Equal(privileges.Order(), granted.Select(g => g.Privilege).Order());

            foreach (var grant in granted)
            {
                await _session.ExecuteDdlAsync(GrantDdl.Revoke(Own(name), grant).Sql, Ct);
            }

            Assert.DoesNotContain(await Reader.GetGrantsAsync(Own(name), Ct), g => g.Grantee == Scratch);
        }
    }

    /// <summary>Oracle has no revoke per column (ORA-01750): the one per object takes the privilege on every column.</summary>
    [Fact]
    public async Task A_revoke_takes_the_privilege_on_every_column()
    {
        var table = Own("GR_VIEW");
        await _session!.ExecuteDdlAsync(GrantDdl.Grant(table, ["UPDATE"], ["NAME"], Scratch, grantOption: true).Sql, Ct);
        await _session.ExecuteDdlAsync(GrantDdl.Grant(table, ["UPDATE"], ["ID"], Scratch, grantOption: true).Sql, Ct);
        Assert.Equal(["ID", "NAME"], Assert.Single(await Reader.GetGrantsAsync(table, Ct), g => g.Grantee == Scratch).Columns.Order());

        await _session.ExecuteDdlAsync(GrantDdl.Revoke(table, new ObjectGrant(Scratch, "UPDATE", ["NAME"], _owner, true, false)).Sql, Ct);

        Assert.DoesNotContain(await Reader.GetGrantsAsync(table, Ct), g => g.Grantee == Scratch);
    }

    [Theory]
    [InlineData("other", "T_PLAIN", "SELECT", Scratch, false, "ORA-01031")]       // no grant option on another's object
    [InlineData("own", "GR_VIEW_PLAIN", "SELECT", Scratch, false, "ORA-01720")]   // the view's table may not be passed on
    [InlineData("own", "GR_TABLE", "SELECT", GrantSchema.OtherRole, true, "ORA-01926")] // a role with grant option
    [InlineData("own", "GR_TABLE", "SELECT", "FS_NOBODY", false, "ORA-01917")]
    public async Task A_refused_grant_comes_with_the_measured_code(string schema, string name, string privilege, string grantee, bool grantOption, string code)
    {
        var target = schema == "own" ? Own(name) : new TableRef(Other, name);

        var refused = await Assert.ThrowsAsync<DatabaseException>(() =>
            _session!.ExecuteDdlAsync(GrantDdl.Grant(target, [privilege], [], grantee, grantOption).Sql, Ct));

        Assert.Equal(code, refused.ErrorCode);
    }

    [Theory]
    [InlineData("own", "GR_TABLE", "REFERENCES", Grantee, "ORA-01981")] // the grantee has a foreign key through it
    [InlineData("other", "T_SHARED", "SELECT", Grantee, "ORA-01927")]   // granted by someone else
    public async Task A_refused_revoke_comes_with_the_measured_code(string schema, string name, string privilege, string grantee, string code)
    {
        var target = schema == "own" ? Own(name) : new TableRef(Other, name);

        var refused = await Assert.ThrowsAsync<DatabaseException>(() =>
            _session!.ExecuteDdlAsync(GrantDdl.Revoke(target, new ObjectGrant(grantee, privilege, [], Other, false, false)).Sql, Ct));

        Assert.Equal(code, refused.ErrorCode);
    }
}
