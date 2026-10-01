using FerretSharp.Core.Connections;
using Microsoft.Extensions.Time.Testing;

namespace FerretSharp.Core.Tests.Connections;

public sealed class RecentConnectionsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ferret-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero));

    private string FilePath => Path.Combine(_directory, "recent.json");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Missing_or_corrupt_file_means_no_history()
    {
        var recent = new RecentConnections(FilePath, _time);
        await recent.LoadAsync(Ct);
        Assert.Empty(recent.LastUsed);

        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(FilePath, "garbage", Ct);
        await recent.LoadAsync(Ct);
        Assert.Empty(recent.LastUsed);
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
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
