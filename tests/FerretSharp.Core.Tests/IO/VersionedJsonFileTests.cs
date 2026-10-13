using System.Text.Json;
using FerretSharp.Core.IO;

namespace FerretSharp.Core.Tests.IO;

/// <summary>R5b (review 3.31.0, B2): one way to read FerretSharp's own versioned JSON files; the stores decide what a state means.</summary>
public sealed class VersionedJsonFileTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TestFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private sealed record Doc(int Version, IReadOnlyList<string?>? Items, Shape? Shape = null);

    [System.Text.Json.Serialization.JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
    [System.Text.Json.Serialization.JsonDerivedType(typeof(Circle), "circle")]
    private abstract record Shape;

    private sealed record Circle(int Radius) : Shape;

    private string File1 => _folder.Combine("doc.json");

    private Task<JsonFileRead<Doc>> ReadAsync(string json, Func<Doc, bool>? complete = null)
    {
        File.WriteAllText(File1, json);
        return VersionedJsonFile.ReadAsync(File1, 1, JsonFiles.Options, Ct, complete,
            doc => doc with { Items = (doc.Items ?? []).OfType<string>().ToList() });
    }

    [Fact]
    public async Task A_missing_file_is_missing() =>
        Assert.Equal(JsonFileState.Missing, (await VersionedJsonFile.ReadAsync<Doc>(File1, 1, JsonFiles.Options, Ct)).State);

    [Fact]
    public async Task A_good_file_is_read_and_normalized()
    {
        var read = await ReadAsync("""{ "version": 1, "items": ["a", null, "b"] }""");

        Assert.Equal((JsonFileState.Ok, 1), (read.State, read.Version));
        Assert.Equal(["a", "b"], read.Document!.Items!);
    }

    [Fact]
    public async Task Null_collections_become_empty() =>
        Assert.Empty((await ReadAsync("""{ "version": 1, "items": null }""")).Document!.Items!);

    /// <summary>The version first: a newer file with a shape this FerretSharp cannot read is newer, not broken.</summary>
    [Fact]
    public async Task A_newer_file_is_not_deserialized()
    {
        var read = await ReadAsync("""{ "version": 2, "items": 42, "shape": { "type": "square" } }""");

        Assert.Equal((JsonFileState.Newer, 2, null), (read.State, read.Version, read.Document));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("""{ "version": 1 }""")]
    public async Task No_document_or_an_incomplete_one_is_empty(string json) =>
        Assert.Equal(JsonFileState.Empty, (await ReadAsync(json, complete: d => d.Items is not null)).State);

    [Theory]
    [InlineData("{ kaputt")]
    [InlineData("""{ "version": 1, "items": 42 }""")]
    [InlineData("""{ "version": 1, "items": [], "shape": { "radius": 3 } }""")] // no "type": NotSupportedException
    public async Task No_json_or_a_shape_that_does_not_fit_is_broken(string json)
    {
        var read = await ReadAsync(json);

        Assert.Equal(JsonFileState.Broken, read.State);
        Assert.NotNull(read.Error);
    }

    [Fact]
    public async Task A_locked_file_is_unreadable()
    {
        File.WriteAllText(File1, """{ "version": 1, "items": [] }""");
        using var locked = new FileStream(File1, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var read = await VersionedJsonFile.ReadAsync<Doc>(File1, 1, JsonFiles.Options, Ct);

        Assert.Equal(JsonFileState.Unreadable, read.State);
        Assert.IsAssignableFrom<IOException>(read.Error);
    }

    [Fact]
    public void A_copy_of_a_file_is_kept_once()
    {
        File.WriteAllText(File1, "{ kaputt");
        var now = new DateTimeOffset(2026, 10, 14, 9, 30, 0, TimeSpan.Zero);

        var copy = VersionedJsonFile.KeepCopy(File1, now);
        var again = VersionedJsonFile.KeepCopy(File1, now.AddMinutes(1));

        Assert.Equal(File1 + ".bad-20261014-093000", copy);
        Assert.Equal(copy, again);
        Assert.Equal("{ kaputt", File.ReadAllText(copy));
    }
}
