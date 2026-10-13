using System.IO.Compression;
using System.Text;
using FerretSharp.Core.Connections.Transfer;

namespace FerretSharp.Core.Tests.Connections.Transfer;

/// <summary>
/// Connection files as DBeaver and SQL Developer write them (WP-35). The shapes follow real files of the user; names,
/// hosts and users are made up. DBeaver's credentials are encrypted here by the test itself – no real credentials file
/// is part of the repository.
/// </summary>
internal static class ImportSamples
{
    /// <summary>SQL Developer's "Export Connections" (shape of a real export, without password).</summary>
    public const string SqlDeveloperExport = """
        {"connections":[{"info":{"role":"","SavePassword":"false","OracleConnectionType":"BASIC","PROXY_TYPE":"USER NAME","RaptorConnectionType":"Oracle","serviceName":"ERPT.example.com","customUrl":"jdbc:oracle:thin:@//db-test.example.com:1521/ERPT.example.com","oraDriverType":"thin","NoPasswordConnection":"TRUE","hostname":"db-test.example.com","driver":"oracle.jdbc.OracleDriver","subtype":"oraJDBC","port":"1521","OS_AUTHENTICATION":"false","IS_PROXY":"false","KERBEROS_AUTHENTICATION":"false","user":"erp_app","PROXY_USER_NAME":""},"name":"ERP - Testsystem","type":"jdbc"}]}
        """;

    /// <summary>
    /// SQL Developer's own <c>connections.json</c>: same format, here with a saved password (made up), a SID, and the
    /// kinds of connections FerretSharp cannot use.
    /// </summary>
    public const string SqlDeveloperInstalled = """
        {"connections":[
          {"info":{"role":"","SavePassword":"true","OracleConnectionType":"BASIC","RaptorConnectionType":"Oracle","sid":"KASSE","customUrl":"jdbc:oracle:thin:@kasse-prd:1522:KASSE","NoPasswordConnection":"TRUE","oraDriverType":"thin","password":"NOT-A-REAL-PASSWORD-SQLDEV","hostname":"kasse-prd","driver":"oracle.jdbc.OracleDriver","subtype":"oraJDBC","port":"1522","OS_AUTHENTICATION":"false","IS_PROXY":"false","user":"kasse_read","KERBEROS_AUTHENTICATION":"false","PROXY_USER_NAME":""},"name":"Kasse Prod","type":"jdbc"},
          {"info":{"role":"","OracleConnectionType":"TNS","RaptorConnectionType":"Oracle","customUrl":"jdbc:oracle:thin:@REPDB","driver":"oracle.jdbc.OracleDriver","user":"rep","OS_AUTHENTICATION":"false","IS_PROXY":"false","KERBEROS_AUTHENTICATION":"false"},"name":"Reporting","type":"jdbc"},
          {"info":{"role":"","OracleConnectionType":"BASIC","RaptorConnectionType":"Oracle","serviceName":"DEV1","hostname":"dev-db","port":"1521","driver":"oracle.jdbc.OracleDriver","user":"ops","OS_AUTHENTICATION":"true","IS_PROXY":"false","KERBEROS_AUTHENTICATION":"false"},"name":"Ops (OS)","type":"jdbc"},
          {"info":{"role":"SYSDBA","OracleConnectionType":"BASIC","RaptorConnectionType":"Oracle","serviceName":"DEV1","hostname":"dev-db","port":"1521","driver":"oracle.jdbc.OracleDriver","user":"sys","OS_AUTHENTICATION":"false","IS_PROXY":"false","KERBEROS_AUTHENTICATION":"false"},"name":"Dev SYS","type":"jdbc"},
          {"info":{"RaptorConnectionType":"MySQL","hostname":"web-db","port":"3306","driver":"com.mysql.cj.jdbc.Driver","user":"web"},"name":"Webshop","type":"jdbc"}
        ]}
        """;

    /// <summary>A project's <c>data-sources.json</c> (shape of a real one) with the cases FerretSharp distinguishes.</summary>
    public const string DbeaverDataSources = """
        {
            "folders": { "ERP": {} },
            "connections": {
                "oracle_thin-1111-aaaa": {
                    "provider": "oracle", "driver": "oracle_thin", "name": "erp-test", "folder": "ERP",
                    "configuration": {
                        "host": "db-test.example.com", "port": "1532", "database": "ERPT.example.com",
                        "url": "jdbc:oracle:thin:@//db-test.example.com:1532/ERPT.example.com",
                        "configurationType": "MANUAL", "type": "dev", "closeIdleConnection": true,
                        "provider-properties": { "@dbeaver-sid-service@": "SERVICE", "@dbeaver-connection-type@": "BASIC" }
                    }
                },
                "oracle_thin-2222-bbbb": {
                    "provider": "oracle", "driver": "oracle_thin", "name": "erp-live", "read-only": true,
                    "configuration": {
                        "host": "db-prod", "port": "1521", "database": "ERPP", "type": "prod",
                        "provider-properties": { "@dbeaver-sid-service@": "SID", "@dbeaver-connection-type@": "BASIC" }
                    }
                },
                "oracle_thin-3333-cccc": {
                    "provider": "oracle", "driver": "oracle_thin", "name": "kasse",
                    "configuration": {
                        "url": "jdbc:oracle:thin:@//kasse-db:1522/KQS", "configurationType": "URL", "type": "abnahme",
                        "provider-properties": { "@dbeaver-connection-type@": "CUSTOM" }
                    }
                },
                "oracle_thin-4444-dddd": {
                    "provider": "oracle", "driver": "oracle_thin", "name": "reporting",
                    "configuration": {
                        "database": "REPDB", "type": "dev",
                        "provider-properties": { "@dbeaver-connection-type@": "TNS" }
                    }
                },
                "postgres-jdbc-5555-eeee": {
                    "provider": "postgresql", "driver": "postgres-jdbc", "name": "web",
                    "configuration": { "host": "web-db", "port": "5432", "database": "web", "type": "dev" }
                }
            },
            "connection-types": {
                "dev": { "name": "Development", "auto-commit": true },
                "abnahme": { "name": "QS Abnahme", "auto-commit": false }
            }
        }
        """;

    /// <summary>The ids of <see cref="DbeaverDataSources"/> with users – and for one a password, which must never come out.</summary>
    public const string DbeaverCredentialsJson = """
        {"oracle_thin-1111-aaaa":{"#connection":{"user":"erp_app","password":"NOT-A-REAL-PASSWORD-DBEAVER"}},"oracle_thin-2222-bbbb":{"#connection":{"user":"erp_read"}}}
        """;

    public const string FakePasswordSqlDeveloper = "NOT-A-REAL-PASSWORD-SQLDEV";

    public const string FakePasswordDbeaver = "NOT-A-REAL-PASSWORD-DBEAVER";

    public static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    /// <summary>What DBeaver writes into <c>credentials-config.json</c> for <see cref="DbeaverCredentialsJson"/>.</summary>
    public static byte[] DbeaverCredentialsFile(string json = DbeaverCredentialsJson) =>
        Core.Connections.Transfer.DbeaverCredentials.EncryptForTests(json, [.. Enumerable.Range(1, 16).Select(i => (byte)i)]);

    /// <summary>A DBeaver project export (<c>.dbp</c>) with the given projects, each with data sources and credentials.</summary>
    public static byte[] DbeaverExport(params string[] projects)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var project in projects)
            {
                zip.CreateEntry($"projects/{project}/");
                Add(zip, $"projects/{project}/.dbeaver/data-sources.json", Utf8(DbeaverDataSources));
                Add(zip, $"projects/{project}/.dbeaver/credentials-config.json", DbeaverCredentialsFile());
                Add(zip, $"projects/{project}/.settings/org.eclipse.core.resources.prefs", Utf8("eclipse.preferences.version=1"));
            }

            Add(zip, "meta.xml", Utf8("""<?xml version="1.0" encoding="UTF-8"?><archive version="1"><source host="TEST-PC"/></archive>"""));
        }

        return buffer.ToArray();
    }

    private static void Add(ZipArchive zip, string name, byte[] content)
    {
        using var stream = zip.CreateEntry(name).Open();
        stream.Write(content);
    }

    public static ImportCandidate Named(this ImportFile file, string name) => Assert.Single(file.Candidates, c => c.Name == name);
}
