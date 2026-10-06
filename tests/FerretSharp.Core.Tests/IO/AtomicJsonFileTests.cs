using System.Text.Json;
using FerretSharp.Core.IO;

namespace FerretSharp.Core.Tests.IO;

public sealed class AtomicJsonFileTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ferretsharp-tests", Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Doc(int Version, string Text);

    private sealed class Broken
    {
        public string Text => throw new InvalidOperationException("kaputt");
    }

    [Fact]
    public async Task Creates_the_directory_and_replaces_the_file()
    {
        var path = Path.Combine(_directory, "sub", "doc.json");

        await AtomicJsonFile.WriteAsync(path, new Doc(1, "eins"), JsonSerializerOptions.Web, Ct);
        await AtomicJsonFile.WriteAsync(path, new Doc(2, "zwei"), JsonSerializerOptions.Web, Ct);

        Assert.Equal(new Doc(2, "zwei"), JsonSerializer.Deserialize<Doc>(await File.ReadAllTextAsync(path, Ct), JsonSerializerOptions.Web));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public async Task A_failed_write_keeps_the_old_file_and_removes_the_temporary_one()
    {
        var path = Path.Combine(_directory, "doc.json");
        await AtomicJsonFile.WriteAsync(path, new Doc(1, "eins"), JsonSerializerOptions.Web, Ct);

        await Assert.ThrowsAsync<InvalidOperationException>(() => AtomicJsonFile.WriteAsync(path, new Broken(), JsonSerializerOptions.Web, Ct));

        Assert.Equal(new Doc(1, "eins"), JsonSerializer.Deserialize<Doc>(await File.ReadAllTextAsync(path, Ct), JsonSerializerOptions.Web));
        Assert.False(File.Exists(path + ".tmp"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
