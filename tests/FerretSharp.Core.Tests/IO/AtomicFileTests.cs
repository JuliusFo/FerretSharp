using FerretSharp.Core.IO;

namespace FerretSharp.Core.Tests.IO;

/// <summary>WP-54: an export written into a temporary file and moved over the target – nothing half-written stays.</summary>
public sealed class AtomicFileTests : IDisposable
{
    private readonly TestFolder _folder = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Writes_the_file_and_returns_the_result()
    {
        var path = _folder.Combine("export.csv");

        var rows = await AtomicFile.WriteAsync(path, async (stream, ct) =>
        {
            await stream.WriteAsync("a;b\r\n"u8.ToArray(), ct);
            return 1;
        }, Ct);

        Assert.Equal(1, rows);
        Assert.Equal("a;b\r\n", await File.ReadAllTextAsync(path, Ct));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public async Task A_cancelled_write_keeps_the_existing_file_and_leaves_no_temporary_one()
    {
        var path = _folder.Combine("export.csv");
        await File.WriteAllTextAsync(path, "alt", Ct);
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AtomicFile.WriteAsync(path, async (stream, ct) =>
        {
            await stream.WriteAsync("halb"u8.ToArray(), ct);
            Assert.True(File.Exists(path + ".tmp")); // written beside the target, not into it
            await cts.CancelAsync();
            ct.ThrowIfCancellationRequested();
            return 0;
        }, cts.Token));

        Assert.Equal("alt", await File.ReadAllTextAsync(path, Ct));
        Assert.False(File.Exists(path + ".tmp"));
    }

    public void Dispose() => _folder.Dispose();
}
