using System.Text.Json;
using FerretSharp.Core.Connections;
using FerretSharp.Core.Connections.Transfer;
using static FerretSharp.Core.Tests.Connections.Transfer.ImportSamples;

namespace FerretSharp.Core.Tests.Connections.Transfer;

public class ImportReaderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ImportFile ReadContent(byte[] content) => ConnectionImportReader.Read(content, Ct);

    [Fact]
    public void SQL_Developer_export_is_read_with_a_guessed_kind()
    {
        var file = ReadContent(Utf8(SqlDeveloperExport));

        Assert.Equal(ConnectionImportSource.SqlDeveloper, file.Source);
        var candidate = file.Named("ERP - Testsystem");
        Assert.True(candidate.KindGuessed);
        var profile = Assert.IsType<ConnectionProfile>(candidate.Profile);
        Assert.Equal(ConnectionKind.Test, profile.Kind);
        Assert.Equal(new HostPortAddress("db-test.example.com", 1521, "ERPT.example.com", null), profile.Address);
        Assert.Equal("erp_app", profile.User);
        Assert.Null(profile.Group);
        Assert.Null(candidate.Unsupported);
    }

    [Fact]
    public void SQL_Developer_installation_file_has_the_same_format_and_its_password_never_comes_out()
    {
        var file = ReadContent(Utf8(SqlDeveloperInstalled));

        var prod = file.Named("Kasse Prod").Profile!;
        Assert.Equal(new HostPortAddress("kasse-prd", 1522, null, "KASSE"), prod.Address);
        Assert.Equal(ConnectionKind.Prod, prod.Kind);
        Assert.Equal("kasse_read", prod.User);
        Assert.DoesNotContain(FakePasswordSqlDeveloper, JsonSerializer.Serialize(file));
    }

    [Fact]
    public void SQL_Developer_connections_FerretSharp_cannot_use_are_shown_with_the_reason()
    {
        var file = ReadContent(Utf8(SqlDeveloperInstalled));

        Assert.Equal("TNS-Verbindungen lassen sich noch nicht importieren.", file.Named("Reporting").Unsupported);
        Assert.Equal("TNS: REPDB", file.Named("Reporting").Address);
        Assert.Equal("Anmeldung über das Betriebssystem wird nicht unterstützt.", file.Named("Ops (OS)").Unsupported);
        Assert.Equal("Anmeldung als SYSDBA wird nicht unterstützt.", file.Named("Dev SYS").Unsupported);
        Assert.All(file.Candidates.Where(c => c.Unsupported is not null), c => Assert.Null(c.Profile));
        Assert.Equal(1, file.OtherDatabases);
        Assert.DoesNotContain(file.Candidates, c => c.Name == "Webshop");
    }

    [Fact]
    public async Task DBeaver_data_sources_take_kind_folder_and_the_users_from_the_credentials_beside_them()
    {
        using var folder = new TestFolder();
        var dbeaver = Directory.CreateDirectory(folder.Combine("workspace6", "General", ".dbeaver")).FullName;
        await File.WriteAllTextAsync(Path.Combine(dbeaver, "data-sources.json"), DbeaverDataSources, Ct);
        await File.WriteAllBytesAsync(Path.Combine(dbeaver, "credentials-config.json"), DbeaverCredentialsFile(), Ct);

        var file = await ConnectionImportReader.ReadFileAsync(Path.Combine(dbeaver, "data-sources.json"), Ct);

        Assert.Equal(ConnectionImportSource.DBeaver, file.Source);
        var test = file.Named("erp-test");
        Assert.False(test.KindGuessed);
        Assert.Equal(ConnectionKind.Dev, test.Profile!.Kind);
        Assert.Equal(new HostPortAddress("db-test.example.com", 1532, "ERPT.example.com", null), test.Profile.Address);
        Assert.Equal("erp_app", test.Profile.User);
        Assert.Equal("ERP", test.Profile.Group);

        var live = file.Named("erp-live").Profile!;
        Assert.Equal(ConnectionKind.Prod, live.Kind);
        Assert.Equal(new HostPortAddress("db-prod", 1521, null, "ERPP"), live.Address);
        Assert.Equal("erp_read", live.User);
        Assert.True(live.ReadOnly);
        Assert.Null(live.Group); // default project: the preview's group applies

        Assert.DoesNotContain(FakePasswordDbeaver, JsonSerializer.Serialize(file));
    }

    [Fact]
    public void DBeaver_own_types_are_guessed_from_their_name_and_URL_connections_read_from_the_URL()
    {
        var file = ReadContent(Utf8(DbeaverDataSources));

        var kasse = file.Named("kasse");
        Assert.True(kasse.KindGuessed);
        Assert.Equal(ConnectionKind.Test, kasse.Profile!.Kind); // type "QS Abnahme"
        Assert.Equal(new HostPortAddress("kasse-db", 1522, "KQS", null), kasse.Profile.Address);
        Assert.Equal("", kasse.Profile.User); // no credentials beside a file read on its own

        Assert.Equal("TNS-Verbindungen lassen sich noch nicht importieren.", file.Named("reporting").Unsupported);
        Assert.Equal(1, file.OtherDatabases);
    }

    [Fact]
    public void DBeaver_project_export_reads_every_project_with_its_credentials_and_the_project_as_group()
    {
        var file = ReadContent(DbeaverExport("General", "Werk Süd"));

        Assert.Equal(ConnectionImportSource.DBeaver, file.Source);
        Assert.Equal(8, file.Candidates.Count);
        Assert.Equal(2, file.OtherDatabases);
        var fromProject = file.Candidates.Where(c => c.Name == "erp-live").Select(c => c.Profile!.Group).ToList();
        Assert.Equal([null, "Werk Süd"], fromProject);
        Assert.All(file.Candidates.Where(c => c.Name == "erp-test"), c => Assert.Equal("ERP", c.Profile!.Group)); // folder wins
        Assert.All(file.Candidates.Where(c => c.Name == "erp-test"), c => Assert.Equal("erp_app", c.Profile!.User));
        Assert.DoesNotContain(FakePasswordDbeaver, JsonSerializer.Serialize(file));
    }

    [Fact]
    public void Credentials_that_cannot_be_decrypted_leave_the_users_empty()
    {
        Assert.Empty(DbeaverCredentials.ReadUsers([1, 2, 3]));
        Assert.Empty(DbeaverCredentials.ReadUsers(new byte[48]));
        Assert.Empty(DbeaverCredentials.ReadUsers(null));
        Assert.Equal("erp_read", DbeaverCredentials.ReadUsers(DbeaverCredentialsFile())["oracle_thin-2222-bbbb"]);
    }

    [Fact]
    public void Credentials_only_yield_users()
    {
        var users = DbeaverCredentials.ReadUsers(DbeaverCredentialsFile());

        Assert.Equal(["erp_app", "erp_read"], users.OrderBy(u => u.Key).Select(u => u.Value));
    }

    [Fact]
    public void Unknown_and_broken_files_are_refused_with_a_message()
    {
        var unknown = Assert.Throws<ConnectionImportException>(() => ReadContent(Utf8("""{"something":[]}""")));
        var broken = Assert.Throws<ConnectionImportException>(() => ReadContent(Utf8("{\"connections\": ")));
        var zip = Assert.Throws<ConnectionImportException>(() => ReadContent([0x50, 0x4b, 0x03, 0x04, 0, 0]));

        Assert.Equal("Die Datei ist keine Verbindungsdatei von FerretSharp, DBeaver oder SQL Developer.", unknown.Message);
        Assert.StartsWith("Die Datei ist kein gültiges JSON:", broken.Message);
        Assert.StartsWith("Die Datei ließ sich nicht lesen:", zip.Message);
    }

    [Fact]
    public void Files_over_the_limit_are_refused()
    {
        var error = Assert.Throws<ConnectionImportException>(() => ReadContent(new byte[17 * 1024 * 1024]));

        Assert.Equal("Die Datei ist zu groß für eine Verbindungsdatei (mehr als 16 MB).", error.Message);
    }

    [Theory]
    [InlineData("ERP Prod", ConnectionKind.Prod)]
    [InlineData("erp_prd_01", ConnectionKind.Prod)]
    [InlineData("Produktion Werk", ConnectionKind.Prod)]
    [InlineData("Shop LIVE", ConnectionKind.Prod)]
    [InlineData("Prod-Kopie für Test", ConnectionKind.Prod)]
    [InlineData("ERP - Testsystem", ConnectionKind.Test)]
    [InlineData("kasse-tst", ConnectionKind.Test)]
    [InlineData("QS Abnahme", ConnectionKind.Test)]
    [InlineData("Development", ConnectionKind.Dev)]
    [InlineData("Entwicklung", ConnectionKind.Dev)]
    [InlineData("erp_dev", ConnectionKind.Dev)]
    [InlineData("ERPQS", ConnectionKind.Other)]
    [InlineData("Device Hub", ConnectionKind.Other)]
    [InlineData("nst10t", ConnectionKind.Other)]
    [InlineData(null, ConnectionKind.Other)]
    public void Kind_is_guessed_from_words_of_the_name(string? name, ConnectionKind expected) =>
        Assert.Equal(expected, ConnectionKindGuess.FromName(name));

    [Theory]
    [InlineData("jdbc:oracle:thin:@//db01:1521/ORCLPDB", "db01", 1521, "ORCLPDB", null)]
    [InlineData("jdbc:oracle:thin:@//db01/ORCLPDB", "db01", 1521, "ORCLPDB", null)]
    [InlineData("jdbc:oracle:thin:@db01.example.com:1522/ERP.example.com", "db01.example.com", 1522, "ERP.example.com", null)]
    [InlineData("jdbc:oracle:thin:@db01:1521:ERP", "db01", 1521, null, "ERP")]
    [InlineData("jdbc:oracle:thin:scott/tiger@//db01:1521/ORCLPDB", "db01", 1521, "ORCLPDB", null)]
    public void JDBC_thin_URLs_are_read(string url, string host, int port, string? service, string? sid) =>
        Assert.Equal(new HostPortAddress(host, port, service, sid), JdbcUrl.TryParse(url));

    [Theory]
    [InlineData("jdbc:oracle:thin:@REPDB")]
    [InlineData("jdbc:oracle:thin:@(DESCRIPTION=(ADDRESS=(HOST=db01)(PORT=1521))(CONNECT_DATA=(SERVICE_NAME=X)))")]
    [InlineData("jdbc:mysql://web-db:3306/web")]
    [InlineData("")]
    public void Other_URLs_are_not_read(string url) => Assert.Null(JdbcUrl.TryParse(url));
}
