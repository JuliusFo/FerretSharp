using System.Text.Json;
using FerretSharp.Core.IO;

namespace FerretSharp.Core.Tests.IO;

public sealed class AtomicJsonFileTests : IDisposable
{
    private readonly TestFolder _folder = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Doc(int Version, string Text);

    private sealed class Broken
    {
        public string Text => throw new InvalidOperationException("kaputt");
    }

    [Fact]
    public async Task Creates_the_directory_and_replaces_the_file()
    {
        var path = _folder.Combine("sub", "doc.json");

        await AtomicJsonFile.WriteAsync(path, new Doc(1, "eins"), JsonSerializerOptions.Web, Ct);
        await AtomicJsonFile.WriteAsync(path, new Doc(2, "zwei"), JsonSerializerOptions.Web, Ct);

        Assert.Equal(new Doc(2, "zwei"), JsonSerializer.Deserialize<Doc>(await File.ReadAllTextAsync(path, Ct), JsonSerializerOptions.Web));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public async Task A_failed_write_keeps_the_old_file_and_removes_the_temporary_one()
    {
        var path = _folder.Combine("doc.json");
        await AtomicJsonFile.WriteAsync(path, new Doc(1, "eins"), JsonSerializerOptions.Web, Ct);

        await Assert.ThrowsAsync<InvalidOperationException>(() => AtomicJsonFile.WriteAsync(path, new Broken(), JsonSerializerOptions.Web, Ct));

        Assert.Equal(new Doc(1, "eins"), JsonSerializer.Deserialize<Doc>(await File.ReadAllTextAsync(path, Ct), JsonSerializerOptions.Web));
        Assert.False(File.Exists(path + ".tmp"));
    }

    public void Dispose()
    {
        _folder.Dispose();
    }
}
