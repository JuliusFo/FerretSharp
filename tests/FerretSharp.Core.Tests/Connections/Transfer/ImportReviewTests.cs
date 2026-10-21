using System.IO.Compression;
using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Connections;
using FerretSharp.Core.Connections.Transfer;
using FerretSharp.Testing;
using NSubstitute;
using static FerretSharp.Core.Tests.Connections.Transfer.ImportSamples;

namespace FerretSharp.Core.Tests.Connections.Transfer;

/// <summary>The findings of the review of WP-35, each pinned by a test.</summary>
public class ImportReviewTests
{
    /// <summary>erp-test of <see cref="ImportSamples.DbeaverDataSources"/>, saved already (as Prod, read-only, with own settings).</summary>
    private static readonly ConnectionProfile SavedProd = new(
        Guid.NewGuid(), "ERP (gespeichert)", ConnectionKind.Prod, new HostPortAddress("db-test.example.com", 1532, "ERPT.example.com", null),
        "erp_app", "ERP", true, "Eigene", new ClrProjectLink(@"C:\mein\Erp.Data.csproj"), @"C:\mein\sql", "erp-prod");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Select_all_leaves_out_duplicates_and_repeats_none_clears_everything()
    {
        var plan = new ConnectionImportPlan(ConnectionImportReader.Read(DbeaverExport("General", "Werk Süd"), Ct), [SavedProd]);
        foreach (var row in plan.Rows.Where(r => r.UserMissing))
        {
            row.User = "app";
        }

        plan.SelectAll(true);

        Assert.All(plan.Rows.Where(r => r.Duplicate is not null || r.SameAsEarlier is not null), r => Assert.False(r.Selected));
        Assert.Equal(2, plan.SelectedRows.Count()); // erp-live and kasse of the first project; the second repeats them
        plan.SelectAll(false);
        Assert.Empty(plan.SelectedRows);
    }

    [Fact]
    public void A_typed_user_that_makes_a_row_a_duplicate_deselects_it()
    {
        var existing = SavedProd with { Address = new HostPortAddress("kasse-db", 1522, "KQS", null), User = "kasse" };
        var plan = new ConnectionImportPlan(ConnectionImportReader.Read(Utf8(DbeaverDataSources), Ct), [existing]);
        var kasse = plan.Rows.Single(r => r.Candidate.Name == "kasse");
        Assert.True(kasse.Selected);

        kasse.User = "KASSE";

        Assert.Same(existing, kasse.Duplicate);
        Assert.False(kasse.Selected);
    }

    [Fact]
    public void Two_rows_overwriting_the_same_connection_block_the_import()
    {
        var plan = new ConnectionImportPlan(ConnectionImportReader.Read(DbeaverExport("General", "Werk Süd"), Ct), [SavedProd]);
        var both = plan.Rows.Where(r => r.Candidate.Name == "erp-test").ToList();
        plan.SelectAll(false);
        both.ForEach(r => r.Selected = true);

        Assert.Equal("„erp-test“ und „erp-test“ würden beide „ERP (gespeichert)“ überschreiben – eine abwählen oder als Kopie anlegen.", plan.Problem);
        Assert.Throws<InvalidOperationException>(() => plan.Build());

        both[1].Action = DuplicateAction.Copy;
        Assert.Null(plan.Problem);
        Assert.Equal(2, plan.Build().Select(p => p.Id).Distinct().Count());
    }

    [Fact]
    public async Task Saving_the_same_id_twice_is_refused()
    {
        var manager = new ConnectionManager(Substitute.For<IConnectionStore>(), new InMemorySecretStore());
        var profile = TestProfiles.HostPort();

        var error = await Assert.ThrowsAsync<ArgumentException>(() => manager.SaveManyAsync([profile, profile with { Name = "Zwei" }], Ct));

        Assert.DoesNotContain("Parameter", error.Message);
        Assert.Empty(manager.Profiles);
    }

    [Fact]
    public void Overwriting_never_loosens_protection_and_keeps_schema_and_local_settings()
    {
        var plan = new ConnectionImportPlan(ConnectionImportReader.Read(Utf8(DbeaverDataSources), Ct), [SavedProd]);
        var row = plan.Rows.Single(r => r.Candidate.Name == "erp-test");
        plan.SelectAll(false);
        row.User = "erp_app"; // now a duplicate of the saved one …
        row.Selected = true; // … picked on purpose

        Assert.Equal(ConnectionKind.Prod, row.Kind); // DBeaver says dev, but the saved one is Prod (review 3.31.0, A5) …
        Assert.False(row.KindGuessed);
        Assert.True(row.ReadOnly); // … and read-only

        var saved = Assert.Single(plan.Build());
        Assert.Equal(SavedProd.Id, saved.Id);
        Assert.True(saved.ReadOnly);
        Assert.Equal(ConnectionKind.Prod, saved.Kind);
        Assert.Equal("ERP", saved.DefaultSchema);
        Assert.Equal(SavedProd.ClrProject, saved.ClrProject);
        Assert.Equal(SavedProd.ScriptFolder, saved.ScriptFolder);
        Assert.Equal("erp-prod", saved.AppEnvironment);

        row.Action = DuplicateAction.Copy; // a copy is a connection of its own: Dev as DBeaver says
        Assert.Equal(ConnectionKind.Dev, row.Kind);
        Assert.False(row.ReadOnly);
    }

    /// <summary>Review 3.31.0, A5: overwriting made a saved Prod connection Dev – only "read-only" was kept.</summary>
    [Fact]
    public void Overwriting_a_prod_connection_changes_its_kind_only_when_picked_on_purpose()
    {
        var plan = new ConnectionImportPlan(ConnectionImportReader.Read(Utf8(DbeaverDataSources), Ct), [SavedProd]);
        var row = plan.Rows.Single(r => r.Candidate.Name == "erp-test");
        plan.SelectAll(false);
        row.User = "erp_app";
        row.Selected = true;

        row.SetKind(ConnectionKind.Test);

        Assert.Equal(ConnectionKind.Test, row.Kind);
        Assert.True(row.ReadOnly); // still never loosened
        Assert.Equal(ConnectionKind.Test, Assert.Single(plan.Build()).Kind);
    }

    /// <summary>Review 3.31.0, A4: an import took the C# project, script folder and TNS_ADMIN of the source's computer unseen.</summary>
    [Fact]
    public void Local_paths_are_taken_only_when_asked_for()
    {
        var colleague = SavedProd with
        {
            Id = Guid.NewGuid(), Name = "Neu", Address = new TnsAliasAddress("ERP_NEU", @"\server	ns"),
            ClrProject = new ClrProjectLink(@"\servershareErp.Data.csproj"), ScriptFolder = @"D:kollegesql",
        };
        var file = Utf8(FerretSharpConnections.Export([colleague], includeLocalPaths: true));
        var plan = new ConnectionImportPlan(ConnectionImportReader.Read(file, Ct), []);
        var row = Assert.Single(plan.Rows);

        Assert.True(plan.HasLocalPaths);
        Assert.Equal([@"\servershareErp.Data.csproj", @"D:kollegesql", @"\server	ns"], row.LocalPaths);
        var plain = Assert.Single(plan.Build());
        Assert.Null(plain.ClrProject);
        Assert.Null(plain.ScriptFolder);
        Assert.Equal(new TnsAliasAddress("ERP_NEU", null), plain.Address);

        plan.TakeLocalPaths = true; // moving one's own connections
        var moved = Assert.Single(plan.Build());
        Assert.Equal(colleague.ClrProject, moved.ClrProject);
        Assert.Equal(colleague.Address, moved.Address);
    }

    [Fact]
    public void An_export_without_local_paths_leaves_out_tns_admin_too_an_overwrite_keeps_the_own()
    {
        var own = SavedProd with { Address = new TnsAliasAddress("ERP", @"C:oracle
etworkadmin") };
        var exported = FerretSharpConnections.Export([own with { Id = Guid.NewGuid() }], includeLocalPaths: false);
        Assert.DoesNotContain("network", exported, StringComparison.Ordinal);

        var colleague = own with { Id = Guid.NewGuid(), Address = new TnsAliasAddress("ERP", @"\server	ns") };
        var plan = new ConnectionImportPlan(
            ConnectionImportReader.Read(Utf8(FerretSharpConnections.Export([colleague], includeLocalPaths: true)), Ct), [own]);
        Assert.Single(plan.Rows).Selected = true;
        plan.TakeLocalPaths = true;

        Assert.Equal(own.Address, Assert.Single(plan.Build()).Address);
    }

    /// <summary>
    /// Review 3.44.0, A30: an overwritten TNS connection without a TNS_ADMIN of its own (the environment's) took the one of
    /// the file – a tnsnames.ora on a foreign share would have sent the saved password to another listener.
    /// </summary>
    [Fact]
    public void An_overwrite_keeps_the_environment_s_tns_admin_too()
    {
        var own = SavedProd with { Address = new TnsAliasAddress("ERP", null) };
        var colleague = own with { Id = Guid.NewGuid(), Address = new TnsAliasAddress("ERP", @"\\fremd\tns") };
        var plan = new ConnectionImportPlan(
            ConnectionImportReader.Read(Utf8(FerretSharpConnections.Export([colleague], includeLocalPaths: true)), Ct), [own]);
        Assert.Single(plan.Rows).Selected = true;
        plan.TakeLocalPaths = true;

        var saved = Assert.Single(plan.Build());

        Assert.Equal(own.Id, saved.Id);
        Assert.Equal(new TnsAliasAddress("ERP", null), saved.Address);
    }

    [Fact]
    public void Local_paths_of_another_computer_do_not_replace_the_own_ones()
    {
        var colleague = SavedProd with
        {
            Id = Guid.NewGuid(), ClrProject = new ClrProjectLink(@"D:\kollege\Erp.Data.csproj"), ScriptFolder = @"D:\kollege\sql",
        };
        var plan = new ConnectionImportPlan(
            ConnectionImportReader.Read(Utf8(FerretSharpConnections.Export([colleague], includeLocalPaths: true)), Ct), [SavedProd]);
        Assert.Single(plan.Rows).Selected = true;

        var saved = Assert.Single(plan.Build());

        Assert.Equal(@"C:\mein\Erp.Data.csproj", saved.ClrProject!.ProjectFile);
        Assert.Equal(@"C:\mein\sql", saved.ScriptFolder);
    }

    [Fact]
    public void A_guessed_kind_can_be_confirmed_as_it_is()
    {
        var plan = new ConnectionImportPlan(ConnectionImportReader.Read(Utf8(SqlDeveloperExport), Ct), []);
        var row = Assert.Single(plan.Rows);

        row.SetKind(row.Kind);

        Assert.False(row.KindGuessed);
        Assert.False(row.ReadOnly);
    }

    [Fact]
    public void Values_FerretSharp_does_not_accept_show_in_the_preview_instead_of_failing_the_save()
    {
        const string File = """
            { "connections": [ { "info": { "OracleConnectionType": "BASIC", "hostname": "db01", "port": "99999", "serviceName": "S", "user": "u" }, "name": "Falscher Port" } ] }
            """;
        var plan = new ConnectionImportPlan(ConnectionImportReader.Read(Utf8(File), Ct), []);
        var row = Assert.Single(plan.Rows);

        Assert.False(row.CanImport);
        Assert.False(row.Selected);
        Assert.Equal("Port muss zwischen 1 und 65535 liegen.", row.Unsupported);
    }

    [Fact]
    public void Credentials_in_a_JDBC_URL_are_never_shown()
    {
        const string File = """
            { "connections": [
              { "info": { "OracleConnectionType": "TNS", "customUrl": "jdbc:oracle:thin:scott/tiger@REPDB", "user": "scott" }, "name": "TNS" },
              { "info": { "OracleConnectionType": "CUSTOM", "customUrl": "jdbc:oracle:thin:scott/tiger@(DESCRIPTION=(ADDRESS=(HOST=db01)))", "user": "scott" }, "name": "Descriptor" } ] }
            """;

        var file = ConnectionImportReader.Read(Utf8(File), Ct);

        Assert.Equal("TNS: REPDB", file.Named("TNS").Address);
        Assert.Equal("jdbc:oracle:thin:@(DESCRIPTION=(ADDRESS=(HOST=db01)))", file.Named("Descriptor").Address);
        Assert.All(file.Candidates, c => Assert.DoesNotContain("tiger", c.Address));
        Assert.Equal("jdbc:oracle:thin:@//db01:1521/S", JdbcUrl.ForDisplay("jdbc:oracle:thin:a/b@//db01:1521/S"));
        Assert.Equal("jdbc:mysql://web", JdbcUrl.ForDisplay("jdbc:mysql://web"));
        Assert.Equal("jdbc:oracle:thin:@//db01:1521/S", JdbcUrl.ForDisplay("jdbc:oracle:thin:scott/p@ss@//db01:1521/S")); // A42
        Assert.Equal("jdbc:oracle:oci:@REPDB", JdbcUrl.ForDisplay("jdbc:oracle:oci:scott/tiger@REPDB"));
    }

    /// <summary>Review 3.31.0, A45: a data-sources file whose root is no object ended the import with a technical error.</summary>
    [Fact]
    public void A_project_export_with_a_damaged_data_sources_file_is_reported_as_unknown()
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            zip.CreateEntry("projects/General/");
            using var stream = zip.CreateEntry("projects/General/.dbeaver/data-sources.json").Open();
            stream.Write(Utf8("[ 1, 2 ]"));
        }

        var error = Assert.Throws<ConnectionImportException>(() => ConnectionImportReader.Read(buffer.ToArray(), Ct));

        Assert.DoesNotContain("InvalidOperation", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_with_a_byte_order_mark_is_read()
    {
        byte[] content = [0xef, 0xbb, 0xbf, .. Utf8(SqlDeveloperExport)];

        Assert.Single(ConnectionImportReader.Read(content, Ct).Candidates);
    }

    [Fact]
    public void A_FerretSharp_file_with_a_broken_address_or_a_newer_format_is_refused_with_a_message()
    {
        const string NoType = """{ "format": "ferretsharp-connections", "version": 1, "connections": [ { "name": "X", "kind": "dev", "address": { "alias": "A" }, "user": "u" } ] }""";
        const string Newer = """{ "format": "ferretsharp-connections", "version": 2, "connections": [ { "name": "X", "kind": "dev", "address": { "type": "ldap", "url": "x" }, "user": "u" } ] }""";

        var broken = Assert.Throws<ConnectionImportException>(() => ConnectionImportReader.Read(Utf8(NoType), Ct));
        var newer = Assert.Throws<ConnectionImportException>(() => ConnectionImportReader.Read(Utf8(Newer), Ct));

        Assert.StartsWith("Die Datei ist kein gültiges JSON:", broken.Message);
        Assert.Equal("Die Datei stammt aus einer neueren Version von FerretSharp (Format 2).", newer.Message);
    }

    [Fact]
    public void A_damaged_project_export_and_one_with_too_many_entries_are_refused()
    {
        var export = DbeaverExport("General");
        var truncated = export[..(export.Length / 2)];

        var damaged = Assert.Throws<ConnectionImportException>(() => ConnectionImportReader.Read(truncated, Ct));
        var many = Assert.Throws<ConnectionImportException>(() => ConnectionImportReader.Read(ZipWithEntries(10_001), Ct));

        Assert.StartsWith("Die Datei ließ sich nicht lesen:", damaged.Message);
        Assert.Equal("Die Datei ist zu groß für eine Verbindungsdatei (mehr als 16 MB).", many.Message);
    }

    [Fact]
    public void Reading_a_project_export_can_be_canceled()
    {
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        Assert.Throws<OperationCanceledException>(() => ConnectionImportReader.Read(DbeaverExport("General"), canceled.Token));
    }

    [Fact]
    public async Task The_connection_dialog_sees_only_saved_passwords()
    {
        var manager = new ConnectionManager(Substitute.For<IConnectionStore>(), new InMemorySecretStore());
        var profile = TestProfiles.HostPort();
        await manager.SaveAsync(profile, null, Ct);

        manager.SetPassword(profile.Id, "nur-jetzt", remember: false);

        Assert.Equal("nur-jetzt", manager.GetPassword(profile.Id));
        Assert.Null(manager.GetStoredPassword(profile.Id));
    }

    private static byte[] ZipWithEntries(int count)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            for (var i = 0; i < count; i++)
            {
                zip.CreateEntry($"projects/P/{i}");
            }
        }

        return buffer.ToArray();
    }
}
