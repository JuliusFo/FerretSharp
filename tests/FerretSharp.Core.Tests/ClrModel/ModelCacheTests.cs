using System.Globalization;
using System.Text.Json;
using FerretSharp.Core.ClrModel;

namespace FerretSharp.Core.Tests.ClrModel;

public sealed class ModelCacheTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ferret-modelcache-").FullName;
    private readonly string _bin;
    private readonly string _host;
    private readonly BuildOutput _output;
    private readonly ClrProjectLink _link;

    private static readonly ModelExport Model = new(ModelExport.CurrentFormatVersion, "8.0.11", "Shop.Ctx", "options", null,
        [new EntityExport("Shop.Kunde", "Shop.Kunde", false, null, "KUNDEN", null, null, null,
            [new PropertyExport("KundeId", "int", "System.Int32", false, false, "KUNDE_ID", null, null, null, false, null)],
            ["KundeId"], [], "Kunden")]);

    public ModelCacheTests()
    {
        _bin = Directory.CreateDirectory(Path.Combine(_root, "bin", "Debug", "net8.0")).FullName;
        Write(Path.Combine(_bin, "Shop.Data.dll"), "data");
        Write(Path.Combine(_bin, "Shop.Entities.dll"), "entities");
        Write(Path.Combine(_bin, "Shop.Data.deps.json"), "{}");
        Write(Path.Combine(_bin, "Shop.Data.pdb"), "pdb");
        _host = Path.Combine(_root, "host", "FerretSharp.ModelHost.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(_host)!);
        Write(_host, "host");
        _output = new BuildOutput(Path.Combine(_bin, "Shop.Data.dll"), Path.Combine(_bin, "Shop.Data.deps.json"), new Version(8, 0),
            DateTime.UtcNow, [], [], null);
        _link = new ClrProjectLink(Path.Combine(_root, "Shop.Data.csproj"));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ModelCache Cache(string culture = "de-DE") =>
        new(Path.Combine(_root, "cache"), _host, CultureInfo.GetCultureInfo(culture));

    private static void Write(string path, string content)
    {
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc));
    }

    private static string Json(ModelExport model) => JsonSerializer.Serialize(model, ModelHostResult.JsonOptions);

    [Fact]
    public async Task A_saved_model_comes_back_while_the_build_is_unchanged()
    {
        var before = DateTimeOffset.Now;
        await Cache().SaveAsync(_link, _output, Model, Ct);

        var cached = await Cache().TryLoadAsync(_link, _output, Ct);

        Assert.NotNull(cached);
        Assert.Equal(Json(Model), Json(cached.Model));
        Assert.InRange(cached.ExportedAt, before.AddSeconds(-1), DateTimeOffset.Now.AddSeconds(1));
        Assert.Single(Directory.GetFiles(Path.Combine(_root, "cache"))); // no temp file left
    }

    [Fact]
    public async Task Nothing_cached_means_no_model() => Assert.Null(await Cache().TryLoadAsync(_link, _output, Ct));

    public static TheoryData<string> Changes => ["dll", "new satellite", "deleted", "deps", "host"];

    [Theory]
    [MemberData(nameof(Changes))]
    public async Task A_changed_build_or_host_invalidates_it(string change)
    {
        await Cache().SaveAsync(_link, _output, Model, Ct);
        switch (change)
        {
            case "dll":
                File.SetLastWriteTimeUtc(Path.Combine(_bin, "Shop.Entities.dll"), DateTime.UtcNow);
                break;
            case "new satellite":
                Directory.CreateDirectory(Path.Combine(_bin, "de"));
                Write(Path.Combine(_bin, "de", "Shop.Entities.resources.dll"), "de");
                break;
            case "deleted":
                File.Delete(Path.Combine(_bin, "Shop.Entities.dll"));
                break;
            case "deps":
                Write(Path.Combine(_bin, "Shop.Data.deps.json"), "{ \"changed\": true }");
                break;
            case "host":
                Write(_host, "host 3.1");
                break;
        }

        Assert.Null(await Cache().TryLoadAsync(_link, _output, Ct));
    }

    [Fact]
    public async Task Files_the_model_does_not_depend_on_are_ignored()
    {
        await Cache().SaveAsync(_link, _output, Model, Ct);
        File.SetLastWriteTimeUtc(Path.Combine(_bin, "Shop.Data.pdb"), DateTime.UtcNow);
        Write(Path.Combine(_bin, "log.txt"), "written by someone");

        Assert.NotNull(await Cache().TryLoadAsync(_link, _output, Ct));
    }

    [Fact]
    public async Task Culture_and_link_are_part_of_the_key()
    {
        await Cache().SaveAsync(_link, _output, Model, Ct);

        Assert.Null(await Cache("en-US").TryLoadAsync(_link, _output, Ct)); // other display texts
        Assert.Null(await Cache().TryLoadAsync(_link with { ContextType = "Shop.OtherCtx" }, _output, Ct));
        Assert.Null(await Cache().TryLoadAsync(_link with { Configuration = "Release" }, _output, Ct));
        Assert.NotNull(await Cache().TryLoadAsync(_link with { ProjectFile = _link.ProjectFile.ToUpperInvariant() }, _output, Ct));
    }

    [Fact]
    public async Task A_broken_file_is_no_model()
    {
        await Cache().SaveAsync(_link, _output, Model, Ct);
        var file = Assert.Single(Directory.GetFiles(Path.Combine(_root, "cache")));
        await File.WriteAllTextAsync(file, "{ not json", Ct);

        Assert.Null(await Cache().TryLoadAsync(_link, _output, Ct));
    }

    [Fact]
    public async Task A_model_of_another_format_version_is_not_used()
    {
        await Cache().SaveAsync(_link, _output, Model with { FormatVersion = ModelExport.CurrentFormatVersion + 1 }, Ct);

        Assert.Null(await Cache().TryLoadAsync(_link, _output, Ct));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
