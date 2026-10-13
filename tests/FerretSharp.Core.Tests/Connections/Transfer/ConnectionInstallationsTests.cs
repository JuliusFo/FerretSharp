using FerretSharp.Core.Connections;
using FerretSharp.Core.Connections.Transfer;
using static FerretSharp.Core.Tests.Connections.Transfer.ImportSamples;

namespace FerretSharp.Core.Tests.Connections.Transfer;

public class ConnectionInstallationsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Nothing_installed_finds_nothing()
    {
        using var appData = new TestFolder();

        Assert.Empty(ConnectionInstallations.Find(appData.Path));
        Assert.Empty(ConnectionInstallations.Find(appData.Combine("does-not-exist")));
    }

    [Fact]
    public async Task DBeaver_projects_with_connections_are_found_default_project_first()
    {
        using var appData = new TestFolder();
        await DbeaverProject(appData, "Werk Süd");
        await DbeaverProject(appData, "General");
        Directory.CreateDirectory(appData.Combine("DBeaverData", "workspace6", ".metadata"));
        Directory.CreateDirectory(appData.Combine("DBeaverData", "workspace6", "Leer", ".dbeaver"));

        var dbeaver = Assert.Single(ConnectionInstallations.Find(appData.Path));

        Assert.Equal(ConnectionImportSource.DBeaver, dbeaver.Source);
        Assert.Equal(["General", "Werk Süd"], dbeaver.Projects);
        Assert.Equal("General, Werk Süd", dbeaver.Detail);
    }

    [Fact]
    public async Task DBeaver_installation_reads_every_project_with_its_users()
    {
        using var appData = new TestFolder();
        await DbeaverProject(appData, "General");
        await DbeaverProject(appData, "Werk Süd");
        var dbeaver = Assert.Single(ConnectionInstallations.Find(appData.Path));

        var file = await ConnectionInstallations.ReadAsync(dbeaver, Ct);

        Assert.Equal(8, file.Candidates.Count);
        Assert.Equal(2, file.OtherDatabases);
        Assert.Equal([null, "Werk Süd"], file.Candidates.Where(c => c.Name == "erp-live").Select(c => c.Profile!.Group));
        Assert.All(file.Candidates.Where(c => c.Name == "erp-live"), c => Assert.Equal("erp_read", c.Profile!.User));
    }

    [Fact]
    public async Task SQL_Developer_newest_version_wins_compared_as_numbers()
    {
        using var appData = new TestFolder();
        await SqlDeveloperSystem(appData, "9.9.9", SqlDeveloperExport);
        await SqlDeveloperSystem(appData, "23.1.1.345.2114", SqlDeveloperExport);
        await SqlDeveloperSystem(appData, "24.3.1.347.1826", SqlDeveloperInstalled);
        Directory.CreateDirectory(appData.Combine("SQL Developer", "system25.1.0", "o.jdeveloper.db.connection")); // no file

        var sqlDeveloper = Assert.Single(ConnectionInstallations.Find(appData.Path));
        var file = await ConnectionInstallations.ReadAsync(sqlDeveloper, Ct);

        Assert.Equal("24.3.1.347.1826", sqlDeveloper.Detail);
        Assert.Contains(file.Candidates, c => c.Name == "Kasse Prod");
    }

    [Fact]
    public async Task SQL_Developer_folder_with_version_suffix_from_older_releases_is_found()
    {
        using var appData = new TestFolder();
        var folder = Directory.CreateDirectory(appData.Combine("SQL Developer", "system19.2.1.247.2212", "o.jdeveloper.db.connection.19.2.0.206.2117")).FullName;
        await File.WriteAllTextAsync(Path.Combine(folder, "connections.json"), SqlDeveloperExport, Ct);

        var sqlDeveloper = Assert.Single(ConnectionInstallations.Find(appData.Path));

        Assert.Equal(folder, sqlDeveloper.Folder);
    }

    [Fact]
    public void Versions_compare_as_numbers()
    {
        var comparer = ConnectionInstallations.VersionComparer.Instance;

        Assert.True(comparer.Compare("23.1.1", "9.9.9") > 0);
        Assert.True(comparer.Compare("24.3.1.347.1826", "24.3.1.347.999") > 0);
        Assert.True(comparer.Compare("24.3", "24.3.0.1") < 0);
        Assert.Equal(0, comparer.Compare("24.3", "24.3.0"));
    }

    private static async Task DbeaverProject(TestFolder appData, string project)
    {
        var folder = Directory.CreateDirectory(appData.Combine("DBeaverData", "workspace6", project, ".dbeaver")).FullName;
        await File.WriteAllTextAsync(Path.Combine(folder, "data-sources.json"), DbeaverDataSources, Ct);
        await File.WriteAllBytesAsync(Path.Combine(folder, "credentials-config.json"), DbeaverCredentialsFile(), Ct);
    }

    private static async Task SqlDeveloperSystem(TestFolder appData, string version, string connections)
    {
        var folder = Directory.CreateDirectory(appData.Combine("SQL Developer", "system" + version, "o.jdeveloper.db.connection")).FullName;
        await File.WriteAllTextAsync(Path.Combine(folder, "connections.json"), connections, Ct);
    }
}
