using FerretSharp.Core.IO;

namespace FerretSharp.Core.Tests.IO;

/// <summary>WP-54: an export written into a temporary file and moved over the target – nothing half-written stays.</summary>
public sealed class AtomicFileTests : IDisposable
{
    private readonly TestFolder _folder = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string[] TempFiles() => Directory.GetFiles(_folder.Path, "*.tmp");

    [Fact]
    public async Task Writes_the_file_and_returns_the_result()
    {
        var path = _folder.Combine("export.csv");

        var rows = await AtomicFile.WriteExportAsync(path, async (stream, ct) =>
        {
            await stream.WriteAsync("a;b\r\n"u8.ToArray(), ct);
            return 1;
        }, Ct);

        Assert.Equal(1, rows);
        Assert.Equal("a;b\r\n", await File.ReadAllTextAsync(path, Ct));
        Assert.Empty(TempFiles());
    }

    [Fact]
    public async Task A_cancelled_write_keeps_the_existing_file_and_leaves_no_temporary_one()
    {
        var path = _folder.Combine("export.csv");
        await File.WriteAllTextAsync(path, "alt", Ct);
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AtomicFile.WriteExportAsync(path, async (stream, ct) =>
        {
            await stream.WriteAsync("halb"u8.ToArray(), ct);
            Assert.Single(TempFiles()); // written beside the target, not into it
            await cts.CancelAsync();
            ct.ThrowIfCancellationRequested();
            return 0;
        }, cts.Token));

        Assert.Equal("alt", await File.ReadAllTextAsync(path, Ct));
        Assert.Empty(TempFiles());
    }

    [Fact]
    public async Task A_target_in_use_fails_before_anything_is_written()
    {
        var path = _folder.Combine("export.xlsx");
        await File.WriteAllTextAsync(path, "alt", Ct);
        var written = false;

        await using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) // open in Excel
        {
            await Assert.ThrowsAsync<IOException>(() => AtomicFile.WriteExportAsync(path, (_, _) =>
            {
                written = true;
                return Task.FromResult(0);
            }, Ct));
        }

        Assert.False(written);
        Assert.Equal("alt", await File.ReadAllTextAsync(path, Ct));
    }

    [Fact]
    public async Task A_file_of_the_user_named_like_the_temporary_one_stays()
    {
        var path = _folder.Combine("export.csv");
        await File.WriteAllTextAsync(path + ".tmp", "meins", Ct);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AtomicFile.WriteExportAsync<int>(path, (_, _) => throw new InvalidOperationException("kaputt"), Ct));

        Assert.Equal("meins", await File.ReadAllTextAsync(path + ".tmp", Ct));
        Assert.False(File.Exists(path));
    }

    public void Dispose() => _folder.Dispose();
}
