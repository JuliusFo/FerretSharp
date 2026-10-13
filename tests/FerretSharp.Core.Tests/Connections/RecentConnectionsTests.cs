using FerretSharp.Core.Connections;
using Microsoft.Extensions.Time.Testing;

namespace FerretSharp.Core.Tests.Connections;

public sealed class RecentConnectionsTests : IDisposable
{
    private readonly TestFolder _folder = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero));

    private string FilePath => _folder.Combine("recent.json");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Missing_or_corrupt_file_means_no_history()
    {
        var recent = new RecentConnections(FilePath, _time);
        await recent.LoadAsync(Ct);
        Assert.Empty(recent.LastUsed);

        await File.WriteAllTextAsync(FilePath, "garbage", Ct);
        await recent.LoadAsync(Ct);
        Assert.Empty(recent.LastUsed);
    }

    /// <summary>Review 3.31.0, A6: a file without entries or with an id twice made the shell's start fail.</summary>
    [Theory]
    [InlineData("""{ "version": 1 }""", 0)]
    [InlineData("""{ "version": 1, "entries": [ null ] }""", 0)]
    [InlineData("""{ "version": 1, "entries": [ { "id": "6f1e1b6e-0000-0000-0000-000000000001", "lastUsed": "2026-10-01T09:00:00Z" }, { "id": "6f1e1b6e-0000-0000-0000-000000000001", "lastUsed": "2026-10-02T09:00:00Z" } ] }""", 1)]
    public async Task Files_edited_by_hand_are_read_as_far_as_they_go(string content, int known)
    {
        await File.WriteAllTextAsync(FilePath, content, Ct);
        var recent = new RecentConnections(FilePath, _time);

        await recent.LoadAsync(Ct);

        Assert.Equal(known, recent.LastUsed.Count);
    }

    /// <summary>R5b (review 3.31.0, B2): the version was not checked – a newer FerretSharp's file was replaced at the next connect.</summary>
    [Fact]
    public async Task A_file_of_a_newer_version_is_not_overwritten()
    {
        const string Newer = """{ "version": 2, "entries": [], "pinned": [ "6f1e1b6e-0000-0000-0000-000000000001" ] }""";
        await File.WriteAllTextAsync(FilePath, Newer, Ct);
        var recent = new RecentConnections(FilePath, _time);
        await recent.LoadAsync(Ct);

        await recent.MarkUsedAsync(Guid.NewGuid(), Ct);

        Assert.Single(recent.LastUsed); // in memory for this session
        Assert.Equal(Newer, await File.ReadAllTextAsync(FilePath, Ct));
    }

    [Fact]
    public async Task Marks_usage_persists_and_orders_most_recent_first()
    {
        var a = TestProfiles.HostPort("A");
        var b = TestProfiles.HostPort("B");
        var c = TestProfiles.HostPort("C");
        var recent = new RecentConnections(FilePath, _time);

        await recent.MarkUsedAsync(a.Id, Ct);
        _time.Advance(TimeSpan.FromMinutes(5));
        await recent.MarkUsedAsync(b.Id, Ct);

        var reloaded = new RecentConnections(FilePath, _time);
        await reloaded.LoadAsync(Ct);

        Assert.Equal(["B", "A"], reloaded.MostRecent([a, b, c], 3).Select(p => p.Name));
        Assert.Equal(_time.GetUtcNow(), reloaded.LastUsed[b.Id]);
    }

    public void Dispose()
    {
        _folder.Dispose();
    }
}
