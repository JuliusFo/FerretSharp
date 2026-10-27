using FerretSharp.Core.ClrModel;

namespace FerretSharp.Core.Tests.ClrModel;

/// <summary>
/// The model file (WP-64, ADR 0037): written from a loaded model, read by someone who cannot build the project. Unlike the
/// cache it outlives FerretSharp versions – a file of format 1 stays readable, a newer one is refused with a reason.
/// </summary>
public sealed class ModelFileTests : IDisposable
{
    private readonly TestFolder _folder = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _folder.Dispose();

    private static readonly ModelExport Model = new(ModelExport.CurrentFormatVersion, "8.0.11", "Shop.Data.ShopContext", "options", null,
    [
        new EntityExport("Shop.Kunde", "Shop.Kunde", false, null, "KUNDEN", null, null, null,
        [
            new PropertyExport("KundeId", "int", false, false, "KUNDE_ID", "NUMBER(10)", null, null, false, null),
            new PropertyExport("Status", "KundeStatus", false, false, "STATUS", null, "EnumKuerzelConverter", "string", false,
                [new ValueMapping("Aktiv", "1", "A", "Aktiv"), new ValueMapping("Gesperrt", "2", "G", null)], MaxLength: 1),
        ], ["KundeId"], [], "Kunden"),
    ], "de-DE");

    private static readonly ModelFileOrigin Origin = new("Shop.Data", "Release", null, new DateTimeOffset(2026, 10, 12, 9, 30, 0, TimeSpan.FromHours(2)),
        "release", "a1b2c3d", "3.53.0");

    private string Write(string relative, string content)
    {
        var file = _folder.Combine(relative.Split('/'));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, content);
        return file;
    }

    [Fact]
    public async Task A_written_file_reads_back_with_model_and_origin()
    {
        var path = _folder.Combine("Shop.Data.ferretmodel.json");

        await ModelFile.WriteAsync(path, Model, Origin, Ct);
        var read = await ModelFile.ReadAsync(path, Ct);

        Assert.Null(read.Error);
        Assert.Equal(Origin, read.Origin);
        var kunde = Assert.Single(read.Model!.Entities);
        Assert.Equal("de-DE", read.Model.Culture);
        Assert.Equal("Kunden", kunde.DbSet);
        var status = kunde.Properties[1];
        Assert.Equal(("EnumKuerzelConverter", 1), (status.Converter, status.MaxLength));
        Assert.Equal(Model.Entities[0].Properties[1].Values, status.Values);
    }

    /// <summary>
    /// The contract with older files: this is format 1 as WP-64 introduced it. If this test has to change, the change to
    /// <see cref="ModelExport"/> was not additive – raise <see cref="ModelFile.CurrentVersion"/> instead (ADR 0037).
    /// </summary>
    [Fact]
    public async Task A_file_of_format_one_stays_readable()
    {
        var path = Write("alt.json", """
            {
              "version": 1,
              "origin": { "project": "Shop.Data", "configuration": "Debug", "exportedAt": "2026-10-27T10:00:00+01:00", "branch": "dev", "commit": "0123456", "appVersion": "3.53.0" },
              "model": {
                "formatVersion": 4, "efVersion": "8.0.11", "contextType": "Shop.Ctx", "createdBy": "options", "culture": "en-US",
                "entities": [ { "name": "Shop.Kunde", "clrType": "Shop.Kunde", "isOwned": false, "table": "KUNDEN",
                  "properties": [ { "name": "KundeId", "clrType": "int", "nullable": false, "isShadow": false, "column": "KUNDE_ID", "isFlagsEnum": false } ],
                  "primaryKey": [ "KundeId" ],
                  "foreignKeys": [ { "properties": [ "KundeId" ], "principalEntity": "Shop.Firma", "principalProperties": [ "FirmaId" ], "navigation": "Firma", "isUnique": false } ] } ]
              }
            }
            """);

        var read = await ModelFile.ReadAsync(path, Ct);

        Assert.Null(read.Error);
        Assert.Equal(("Shop.Data", "dev", "0123456"), (read.Origin!.Project, read.Origin.Branch, read.Origin.Commit));
        var kunde = Assert.Single(read.Model!.Entities);
        Assert.Equal("KUNDE_ID", Assert.Single(kunde.Properties).Column);
        Assert.Equal("Firma", Assert.Single(kunde.ForeignKeys).Navigation);
    }

    [Fact]
    public async Task What_cannot_be_used_says_why()
    {
        var missing = await ModelFile.ReadAsync(_folder.Combine("fehlt.json"), Ct);
        var newer = await ModelFile.ReadAsync(Write("neu.json", """{ "version": 2, "kommt": "später" }"""), Ct);
        var broken = await ModelFile.ReadAsync(Write("kaputt.json", "{ halb"), Ct);
        var other = await ModelFile.ReadAsync(Write("settings.json", """{ "version": 1, "theme": "dark" }"""), Ct);

        Assert.Contains("nicht gefunden", missing.Error);
        Assert.Contains("neueren FerretSharp (Format 2)", newer.Error);
        Assert.Contains("kaputt.json ist keine", broken.Error);
        Assert.Contains("settings.json ist keine", other.Error);
        Assert.All(new[] { missing, newer, broken, other }, r => Assert.Null(r.Model));
    }

    [Fact]
    public void The_origin_takes_branch_and_commit_of_the_project_s_working_copy()
    {
        Write("repo/.git/HEAD", "ref: refs/heads/release\n");
        Write("repo/.git/refs/heads/release", "a1b2c3d4e5f60718293a4b5c6d7e8f9012345678\n");
        var project = Write("repo/src/Shop.Data/Shop.Data.csproj", "<Project />");
        var outside = Write("loose/Shop.Data/Shop.Data.csproj", "<Project />");
        var now = DateTimeOffset.Now;

        var origin = ModelFile.OriginOf(new ClrProjectLink(project, "Release", "ShopContext"), now, "3.53.0");
        var plain = ModelFile.OriginOf(new ClrProjectLink(outside), now, "3.53.0");

        Assert.Equal(new ModelFileOrigin("Shop.Data", "Release", "ShopContext", now, "release", "a1b2c3d", "3.53.0"), origin);
        Assert.Equal((null, null), (plain.Branch, plain.Commit));
        Assert.Equal("Shop.Data.ferretmodel.json", ModelFile.SuggestedName(new ClrProjectLink(project)));
    }

    [Fact]
    public void The_commit_comes_from_packed_refs_a_worktree_or_a_detached_head()
    {
        Write("packed/.git/HEAD", "ref: refs/heads/dev\n");
        Write("packed/.git/packed-refs", "# pack-refs with: peeled fully-peeled sorted\n0123456789abcdef0123456789abcdef01234567 refs/heads/dev\n");
        Write("main/.git/HEAD", "ref: refs/heads/main\n");
        Write("main/.git/refs/heads/feature/x", "fedcba9876543210fedcba9876543210fedcba98\n");
        Write("main/.git/worktrees/x/HEAD", "ref: refs/heads/feature/x\n");
        Write("main/.git/worktrees/x/commondir", "../..\n");
        Write("x/.git", "gitdir: ../main/.git/worktrees/x\n");
        Write("detached/.git/HEAD", "abcdef0123456789abcdef0123456789abcdef01");
        Write("unborn/.git/HEAD", "ref: refs/heads/main\n");

        Assert.Equal("0123456", GitFolder.Commit(_folder.Combine("packed")));
        Assert.Equal("fedcba9", GitFolder.Commit(_folder.Combine("x")));
        Assert.Equal("abcdef0", GitFolder.Commit(_folder.Combine("detached")));
        Assert.Null(GitFolder.Commit(_folder.Combine("unborn")));
    }

    [Fact]
    public void Only_a_json_path_is_a_model_file()
    {
        Assert.True(ModelFile.IsModelFile(@"C:\Modelle\Shop.Data.ferretmodel.JSON"));
        Assert.False(ModelFile.IsModelFile(@"C:\src\Shop.Data\Shop.Data.csproj"));
    }

    /// <summary>
    /// Review of WP-64: a model file comes from someone else, and its names end up in generated C# the user copies into a
    /// project. Writing one with a name that would leave an identifier or a comment, or reading one, is refused.
    /// </summary>
    [Theory]
    [InlineData("name", "X = System.Diagnostics.Process.Start(\"calc\")")]
    [InlineData("name", "Id\r\nSystem.IO.File.Delete(\"x\");//")]
    [InlineData("type", "int; Evil()")]
    [InlineData("converter", "JaNein(\"x\")")]
    [InlineData("display", "Aktiv\n}; Evil(); //")]
    [InlineData("dbset", "Kunden.Where(x => Evil())")]
    public async Task A_name_that_would_leave_the_generated_code_is_refused(string where, string value)
    {
        var property = Model.Entities[0].Properties[1];
        var bad = where switch
        {
            "name" => property with { Name = value },
            "type" => property with { ClrType = value },
            "converter" => property with { Converter = value },
            "display" => property with { Values = [new ValueMapping("Aktiv", "1", "A", value)] },
            _ => property,
        };
        var entity = Model.Entities[0] with { Properties = [Model.Entities[0].Properties[0], bad], DbSet = where == "dbset" ? value : "Kunden" };
        var model = Model with { Entities = [entity] };
        var path = _folder.Combine("fremd.json");
        await ModelFile.WriteAsync(path, model, Origin, Ct); // as someone could write it by hand

        var read = await ModelFile.ReadAsync(path, Ct);

        Assert.Null(read.Model);
        Assert.Contains("nicht enthalten darf", read.Error);
        Assert.DoesNotContain('\n', read.Error!);
        Assert.Equal(read.Error, ModelFile.Problem(model)); // the export says the same before writing
    }

    [Fact]
    public void Names_as_the_model_host_writes_them_pass()
    {
        var property = Model.Entities[0].Properties[1] with { Converter = "ValueConverter<bool, string>", ClrType = "Outer+Inner" };
        var entity = Model.Entities[0] with
        {
            Name = "Shop.Auftrag.Lieferadresse#Adresse", ClrType = "Dictionary<string, object>",
            Properties = [Model.Entities[0].Properties[0] with { ClrType = "byte[]" }, property, property with { Name = "@class" }],
        };

        Assert.Null(ModelFile.Problem(Model with { Entities = [entity] }));
    }

    [Fact]
    public async Task A_file_without_required_entries_or_too_large_says_why()
    {
        var missing = Write("ohne.json", """
            { "version": 1, "origin": { "project": "P", "configuration": "Debug", "appVersion": "1" },
              "model": { "efVersion": "8", "contextType": "C", "createdBy": "options", "entities": [ { "name": "E", "clrType": "E" } ] } }
            """);
        var large = _folder.Combine("gross.json");
        await using (var stream = File.Create(large))
        {
            stream.SetLength(ModelFile.MaxBytes + 1);
        }

        Assert.Contains("unvollständig", (await ModelFile.ReadAsync(missing, Ct)).Error);
        Assert.Contains("größer als 64 MB", (await ModelFile.ReadAsync(large, Ct)).Error);
    }

    /// <summary>Review of WP-64: HEAD and commondir of a working copy decide which files are read – never another file or a share.</summary>
    [Fact]
    public void The_commit_reads_only_refs_below_the_git_folder()
    {
        Write("secret.txt", "0123456789abcdef0123456789abcdef01234567");
        Write("up/.git/HEAD", "ref: refs/../../../secret.txt\n"); // .git/refs/../../../ is the folder of secret.txt
        Write("unc/.git/HEAD", @"ref: \\fremd.invalid\share\x" + "\n");
        Write("wt/.git/HEAD", "ref: refs/heads/main\n");
        Write("wt/.git/commondir", @"\\fremd.invalid\share\meta" + "\n");

        Assert.Null(GitFolder.Commit(_folder.Combine("up")));
        Assert.Null(GitFolder.Commit(_folder.Combine("unc")));
        Assert.Null(GitFolder.Commit(_folder.Combine("wt")));
    }
}
