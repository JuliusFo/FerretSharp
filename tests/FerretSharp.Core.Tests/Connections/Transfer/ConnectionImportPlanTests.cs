using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Connections;
using FerretSharp.Core.Connections.Transfer;
using static FerretSharp.Core.Tests.Connections.Transfer.ImportSamples;

namespace FerretSharp.Core.Tests.Connections.Transfer;

public class ConnectionImportPlanTests
{
    private static readonly ConnectionProfile ExistingTest = new(
        Guid.NewGuid(), "Mein ERP-Test", ConnectionKind.Test, new HostPortAddress("DB-TEST.example.com", 1532, "erpt.example.com", null),
        "ERP_APP", null, false, "Eigene", new ClrProjectLink(@"C:\src\Erp\Erp.Data.csproj"), @"C:\src\Erp\sql");

    [Fact]
    public void New_connections_start_selected_duplicates_and_unsupported_ones_do_not()
    {
        var plan = Plan(ConnectionImportReader.Read(DbeaverExport("General")), ExistingTest);

        Assert.Equal(["erp-live", "kasse"], plan.SelectedRows.Select(r => r.Candidate.Name).Order());
        var duplicate = Row(plan, "erp-test");
        Assert.Same(ExistingTest, duplicate.Duplicate);
        Assert.False(duplicate.Selected);
        Assert.False(Row(plan, "reporting").CanImport);
    }

    [Fact]
    public void Guessed_kinds_stay_read_only_until_the_user_picks_one()
    {
        var plan = Plan(ConnectionImportReader.Read(Utf8(SqlDeveloperExport)));
        var row = Assert.Single(plan.Rows);

        Assert.Equal(ConnectionKind.Test, row.Kind);
        Assert.True(row.KindGuessed);
        Assert.True(row.ReadOnly);

        row.SetKind(ConnectionKind.Dev);

        Assert.False(row.KindGuessed);
        Assert.False(row.ReadOnly);
        Assert.False(Assert.Single(plan.Build()).ReadOnly);
    }

    [Fact]
    public void Prod_is_always_read_only_and_so_is_what_the_source_marks_read_only()
    {
        var plan = Plan(ConnectionImportReader.Read(Utf8(DbeaverDataSources)));
        var live = Row(plan, "erp-live");

        Assert.True(live.ReadOnly);
        live.SetKind(ConnectionKind.Dev);
        Assert.True(live.ReadOnly); // DBeaver marks it read-only

        var ferret = Plan(ConnectionImportReader.Read(Utf8(FerretSharpConnections.Export(
            [new ConnectionProfile(Guid.NewGuid(), "P", ConnectionKind.Prod, new HostPortAddress("h", 1521, "S", null), "u", null, ReadOnly: false)],
            includeLocalPaths: false))));
        Assert.True(Assert.Single(ferret.Rows).ReadOnly);
    }

    [Fact]
    public void Missing_users_are_typed_in_and_block_the_import_until_then()
    {
        var plan = Plan(ConnectionImportReader.Read(Utf8(DbeaverDataSources)));
        var kasse = Row(plan, "kasse");

        Assert.True(kasse.UserMissing);
        Assert.Equal("Bei 3 gewählten Verbindungen fehlt der Benutzer – in der Liste eintragen.", plan.Problem);
        Assert.Throws<InvalidOperationException>(() => plan.Build());

        foreach (var row in plan.SelectedRows)
        {
            row.User = " app ";
        }

        Assert.Null(plan.Problem);
        Assert.All(plan.Build(), p => Assert.Equal("app", p.User));
    }

    [Fact]
    public void Nothing_selected_is_a_problem()
    {
        var plan = Plan(ConnectionImportReader.Read(Utf8(SqlDeveloperExport)));
        plan.Rows[0].Selected = false;

        Assert.Equal("Keine Verbindung ausgewählt.", plan.Problem);
    }

    [Fact]
    public void Group_from_the_source_wins_otherwise_the_default_group_or_none()
    {
        var plan = Plan(ConnectionImportReader.Read(DbeaverExport("General", "Werk Süd")));
        Assert.Equal("DBeaver", plan.DefaultGroup);
        foreach (var row in plan.Rows)
        {
            row.Selected = row.CanImport && !row.UserMissing;
        }

        var groups = plan.Build().Where(p => p.Name == "erp-live").Select(p => p.Group).ToList();
        Assert.Equal(["DBeaver", "Werk Süd"], groups);

        plan.DefaultGroup = "  ";
        Assert.Equal([null, "Werk Süd"], plan.Build().Where(p => p.Name == "erp-live").Select(p => p.Group));
    }

    [Fact]
    public void Overwriting_a_duplicate_keeps_its_id_and_local_paths()
    {
        var plan = Plan(ConnectionImportReader.Read(DbeaverExport("General")), ExistingTest);
        var row = Row(plan, "erp-test");
        Row(plan, "kasse").Selected = false; // no user in the file
        row.Selected = true;

        var saved = plan.Build().Single(p => p.Name == "erp-test");

        Assert.Equal(ExistingTest.Id, saved.Id);
        Assert.Equal(ExistingTest.ClrProject, saved.ClrProject);
        Assert.Equal(ExistingTest.ScriptFolder, saved.ScriptFolder);
        Assert.Equal("ERP", saved.Group);
        Assert.Equal(ConnectionKind.Dev, saved.Kind);
    }

    [Fact]
    public void A_copy_of_a_duplicate_is_a_new_connection()
    {
        var plan = Plan(ConnectionImportReader.Read(DbeaverExport("General")), ExistingTest);
        var row = Row(plan, "erp-test");
        Row(plan, "kasse").Selected = false; // no user in the file
        row.Selected = true;
        row.Action = DuplicateAction.Copy;

        var saved = plan.Build().Single(p => p.Name == "erp-test");

        Assert.NotEqual(ExistingTest.Id, saved.Id);
        Assert.Null(saved.ClrProject);
    }

    [Fact]
    public void Duplicates_need_the_same_address_and_user()
    {
        var a = new HostPortAddress("db01", 1521, "ERP", null);

        Assert.True(ConnectionImportPlan.SameAddress(a, new HostPortAddress(" DB01 ", 1521, "erp", "")));
        Assert.False(ConnectionImportPlan.SameAddress(a, new HostPortAddress("db01", 1522, "ERP", null)));
        Assert.False(ConnectionImportPlan.SameAddress(a, new HostPortAddress("db01", 1521, null, "ERP")));
        Assert.True(ConnectionImportPlan.SameAddress(new TnsAliasAddress("erp", null), new TnsAliasAddress("ERP", @"C:\tns")));
        Assert.False(ConnectionImportPlan.SameAddress(a, new TnsAliasAddress("ERP", null)));

        var plan = Plan(ConnectionImportReader.Read(DbeaverExport("General")), ExistingTest with { User = "other" });
        Assert.Null(Row(plan, "erp-test").Duplicate);
    }

    [Fact]
    public void Export_leaves_out_local_paths_unless_asked_and_never_has_passwords()
    {
        var withoutPaths = FerretSharpConnections.Export([ExistingTest], includeLocalPaths: false);
        var withPaths = FerretSharpConnections.Export([ExistingTest], includeLocalPaths: true);

        Assert.Contains("\"format\": \"ferretsharp-connections\"", withoutPaths);
        Assert.DoesNotContain("Erp.Data.csproj", withoutPaths);
        Assert.DoesNotContain("scriptFolder", withoutPaths);
        Assert.Contains("Erp.Data.csproj", withPaths);
        Assert.DoesNotContain("password", withPaths, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Export_reads_back_with_new_ids()
    {
        var file = ConnectionImportReader.Read(Utf8(FerretSharpConnections.Export([ExistingTest], includeLocalPaths: true)));

        Assert.Equal(ConnectionImportSource.FerretSharp, file.Source);
        var imported = Assert.Single(file.Candidates).Profile!;
        Assert.NotEqual(ExistingTest.Id, imported.Id);
        Assert.Equal(ExistingTest with { Id = imported.Id }, imported);
    }

    [Fact]
    public void Another_installations_connections_json_is_read_too_but_not_one_from_a_newer_version()
    {
        const string ConnectionsJson = """
            { "version": 1, "connections": [ { "id": "3f1c3c55-2a3c-4c1f-9a51-0c1f8d6f0a11", "name": "Alt", "kind": "dev",
              "address": { "type": "tnsAlias", "alias": "ERP_DEV" }, "user": "app", "readOnly": false } ] }
            """;

        var file = ConnectionImportReader.Read(Utf8(ConnectionsJson));
        var newer = Assert.Throws<ConnectionImportException>(() => ConnectionImportReader.Read(Utf8(ConnectionsJson.Replace("\"version\": 1", "\"version\": 9"))));

        Assert.Equal(new TnsAliasAddress("ERP_DEV", null), Assert.Single(file.Candidates).Profile!.Address);
        Assert.Equal("Die Datei stammt aus einer neueren Version von FerretSharp (Format 9).", newer.Message);
    }

    private static ConnectionImportPlan Plan(ImportFile file, params ConnectionProfile[] existing) => new(file, existing);

    private static ImportRow Row(ConnectionImportPlan plan, string name) => plan.Rows.Single(r => r.Candidate.Name == name);
}
