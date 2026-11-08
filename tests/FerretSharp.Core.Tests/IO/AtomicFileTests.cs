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

    /// <summary>
    /// Review WP-77: a file in a shared repository is never written through a file of a fixed temporary name – not a link
    /// of that name (it led out of the folder) and not a file of the user's.
    /// </summary>
    [Fact]
    public async Task A_file_in_a_folder_of_the_user_s_is_written_beside_any_file_of_a_temporary_name()
    {
        var path = _folder.Combine("display-columns.json");
        await File.WriteAllTextAsync(path + ".tmp", "my notes", Ct);
        await File.WriteAllTextAsync(path, "old", Ct);

        await AtomicFile.WriteInUserFolderAsync(path, async (stream, ct) =>
        {
            await stream.WriteAsync("new"u8.ToArray(), ct);
            return true;
        }, Ct);

        Assert.Equal("new", await File.ReadAllTextAsync(path, Ct));
        Assert.Equal("my notes", await File.ReadAllTextAsync(path + ".tmp", Ct));

        var outside = _folder.Combine("outside.txt");
        await File.WriteAllTextAsync(outside, "secret", Ct);
        var linked = _folder.Combine("linked.json");
        try
        {
            File.CreateSymbolicLink(linked + ".tmp", outside);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("Creating a symbolic link needs developer mode or admin rights on Windows.");
        }

        await AtomicJsonFile.WriteInUserFolderAsync(linked, new { a = 1 }, System.Text.Json.JsonSerializerOptions.Default, Ct);

        Assert.Equal("secret", await File.ReadAllTextAsync(outside, Ct));
        Assert.Null(new FileInfo(linked).LinkTarget);
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
