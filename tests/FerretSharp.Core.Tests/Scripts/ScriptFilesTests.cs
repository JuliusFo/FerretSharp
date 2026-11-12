using System.Text;
using FerretSharp.Core.Connections;
using FerretSharp.Core.Query;
using FerretSharp.Core.Scripts;

namespace FerretSharp.Core.Tests.Scripts;

/// <summary>WP-77: script files in shared repositories – written atomically, in their own encoding and line breaks.</summary>
public sealed class ScriptFilesTests : IDisposable
{
    private readonly TestFolder _folder = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Root => _folder.Path;

    private static readonly ScriptHeader Header = new("Open orders", [new("id", SqlVariableType.Number)], []);

    [Fact]
    public async Task A_new_script_is_utf8_without_bom_with_crlf_in_a_new_folder()
    {
        var write = await ScriptFiles.WriteAsync(Root, "Orders/Open.sql", Header, "SELECT 'ä' FROM t WHERE id = :id;\n", ScriptWriteMode.New, null, Ct);

        var bytes = await File.ReadAllBytesAsync(_folder.Combine("Orders", "Open.sql"), Ct);
        Assert.Equal(ScriptWriteOutcome.Written, write.Outcome);
        Assert.Equal(ScriptFiles.HashOf(bytes), write.FileHash);
        Assert.Equal(
            "-- @description Open orders\r\n-- @param id number\r\n\r\nSELECT 'ä' FROM t WHERE id = :id;\r\n",
            Encoding.UTF8.GetString(bytes));
        Assert.NotEqual(0xEF, bytes[0]);
        Assert.False(File.Exists(_folder.Combine("Orders", "Open.sql.tmp")));

        var read = await ScriptFiles.ReadAsync(Root, "Orders/Open.sql", Ct);
        Assert.True(read.Script.Header.SameAs(Header));
        Assert.Equal("SELECT 'ä' FROM t WHERE id = :id;\n", read.Script.Body);
        Assert.Equal(write.FileHash, read.FileHash);
    }

    [Fact]
    public async Task A_new_script_does_not_replace_a_file_of_the_same_name()
    {
        await File.WriteAllTextAsync(_folder.Combine("a.sql"), "SELECT 1 FROM dual", Ct);

        var write = await ScriptFiles.WriteAsync(Root, "a.sql", ScriptHeader.Empty, "SELECT 2 FROM dual", ScriptWriteMode.New, null, Ct);

        Assert.Equal(ScriptWriteOutcome.Exists, write.Outcome);
        Assert.Equal("SELECT 1 FROM dual", await File.ReadAllTextAsync(_folder.Combine("a.sql"), Ct));
    }

    [Fact]
    public async Task Saving_over_a_file_changed_since_it_was_read_asks_first()
    {
        await File.WriteAllTextAsync(_folder.Combine("a.sql"), "SELECT 1 FROM dual", Ct);
        var read = await ScriptFiles.ReadAsync(Root, "a.sql", Ct);
        await File.WriteAllTextAsync(_folder.Combine("a.sql"), "SELECT 1 FROM dual -- pulled", Ct);

        var update = await ScriptFiles.WriteAsync(Root, "a.sql", ScriptHeader.Empty, "SELECT 2 FROM dual", ScriptWriteMode.Update, read.FileHash, Ct);
        Assert.Equal(new ScriptWrite(ScriptWriteOutcome.ChangedOnDisk, null), update);
        Assert.Equal("SELECT 1 FROM dual -- pulled", await File.ReadAllTextAsync(_folder.Combine("a.sql"), Ct));

        var replace = await ScriptFiles.WriteAsync(Root, "a.sql", ScriptHeader.Empty, "SELECT 2 FROM dual", ScriptWriteMode.Replace, read.FileHash, Ct);
        Assert.Equal(ScriptWriteOutcome.Written, replace.Outcome);
        Assert.Equal(replace.FileHash, await ScriptFiles.FileHashAsync(Root, "a.sql", Ct));

        File.Delete(_folder.Combine("a.sql"));
        var gone = await ScriptFiles.WriteAsync(Root, "a.sql", ScriptHeader.Empty, "x", ScriptWriteMode.Update, replace.FileHash, Ct);
        Assert.Equal(ScriptWriteOutcome.ChangedOnDisk, gone.Outcome);
    }

    /// <summary>
    /// Review 3.70.0, A23: one CRLF line pasted into an LF script made the whole file CRLF on saving, and git diff showed every
    /// line. The line break most lines have wins, as for C# files.
    /// </summary>
    [Fact]
    public async Task One_crlf_line_in_an_lf_script_does_not_turn_the_file_to_crlf()
    {
        await File.WriteAllTextAsync(_folder.Combine("a.sql"), "SELECT 1\nFROM dual\r\nWHERE 1 = 1\nORDER BY 1\n", Ct);
        var read = await ScriptFiles.ReadAsync(Root, "a.sql", Ct);

        await ScriptFiles.WriteAsync(Root, "a.sql", ScriptHeader.Empty, "SELECT 2\nFROM dual\n", ScriptWriteMode.Update, read.FileHash, Ct);

        Assert.Equal("SELECT 2\nFROM dual\n", await File.ReadAllTextAsync(_folder.Combine("a.sql"), Ct));
    }

    [Theory]
    [InlineData(true, "\n")]
    [InlineData(false, "\r\n")]
    public async Task An_existing_file_keeps_its_bom_and_line_breaks(bool bom, string lineBreak)
    {
        var path = _folder.Combine("a.sql");
        var text = $"SELECT 1{lineBreak}FROM dual;{lineBreak}";
        await File.WriteAllBytesAsync(path, [.. bom ? new byte[] { 0xEF, 0xBB, 0xBF } : [], .. Encoding.UTF8.GetBytes(text)], Ct);
        var read = await ScriptFiles.ReadAsync(Root, "a.sql", Ct);

        await ScriptFiles.WriteAsync(Root, "a.sql", Header, read.Script.Body + "-- ö\n", ScriptWriteMode.Update, read.FileHash, Ct);

        var bytes = await File.ReadAllBytesAsync(path, Ct);
        Assert.Equal(bom, bytes is [0xEF, 0xBB, 0xBF, ..]);
        var written = Encoding.UTF8.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
        Assert.Equal($"-- @description Open orders{lineBreak}-- @param id number{lineBreak}{lineBreak}SELECT 1{lineBreak}FROM dual;{lineBreak}-- ö{lineBreak}", written);
    }

    [Fact]
    public async Task An_ansi_file_stays_ansi_and_refuses_a_character_it_cannot_hold()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var ansi = Encoding.GetEncoding(1252);
        var path = _folder.Combine("a.sql");
        await File.WriteAllBytesAsync(path, ansi.GetBytes("-- Größe\r\nSELECT 1 FROM dual;\r\n"), Ct);
        var read = await ScriptFiles.ReadAsync(Root, "a.sql", Ct);
        Assert.Equal("-- Größe\nSELECT 1 FROM dual;\n", read.Script.Body);

        var written = await ScriptFiles.WriteAsync(Root, "a.sql", ScriptHeader.Empty, read.Script.Body + "-- Maß €\n", ScriptWriteMode.Update, read.FileHash, Ct);
        Assert.Equal(ScriptWriteOutcome.Written, written.Outcome);
        Assert.Equal("-- Größe\r\nSELECT 1 FROM dual;\r\n-- Maß €\r\n", ansi.GetString(await File.ReadAllBytesAsync(path, Ct)));

        await Assert.ThrowsAsync<RefusedException>(() =>
            ScriptFiles.WriteAsync(Root, "a.sql", ScriptHeader.Empty, "-- 漢字", ScriptWriteMode.Replace, null, Ct));
        Assert.Equal("-- Größe\r\nSELECT 1 FROM dual;\r\n-- Maß €\r\n", ansi.GetString(await File.ReadAllBytesAsync(path, Ct)));
    }

    [Fact]
    public async Task A_missing_or_too_large_file_is_refused_on_reading()
    {
        await Assert.ThrowsAsync<RefusedException>(() => ScriptFiles.ReadAsync(Root, "missing.sql", Ct));

        await File.WriteAllBytesAsync(_folder.Combine("big.sql"), new byte[ScriptFiles.MaxBytes + 1], Ct);
        await Assert.ThrowsAsync<RefusedException>(() => ScriptFiles.ReadAsync(Root, "big.sql", Ct));
    }

    [Fact]
    public async Task Deleting_removes_the_file_only_below_the_root()
    {
        await File.WriteAllTextAsync(_folder.Combine("a.sql"), "SELECT 1 FROM dual", Ct);
        await File.WriteAllTextAsync(_folder.Combine("outside.sql"), "SELECT 1 FROM dual", Ct);
        var root = _folder.Combine("root");
        Directory.CreateDirectory(root);

        await ScriptFiles.DeleteAsync(Root, "a.sql", Ct);
        await ScriptFiles.DeleteAsync(Root, "a.sql", Ct); // gone already: nothing to do

        Assert.False(File.Exists(_folder.Combine("a.sql")));
        await Assert.ThrowsAsync<RefusedException>(() => ScriptFiles.DeleteAsync(root, "../outside.sql", Ct));
        Assert.True(File.Exists(_folder.Combine("outside.sql")));
    }

    /// <summary>
    /// Review WP-77, S1: the temporary file had the fixed name <c>x.sql.tmp</c> – a link of that name in a shared folder
    /// had the script written through it to a file outside, and the move turned <c>x.sql</c> into that link. A file of the
    /// user's of that name was replaced.
    /// </summary>
    [Fact]
    public async Task Writing_never_goes_through_a_file_of_the_temporary_name()
    {
        await File.WriteAllTextAsync(_folder.Combine("mine.sql.tmp"), "my notes", Ct);
        await ScriptFiles.WriteAsync(Root, "mine.sql", ScriptHeader.Empty, "SELECT 1 FROM dual", ScriptWriteMode.New, null, Ct);
        Assert.Equal("my notes", await File.ReadAllTextAsync(_folder.Combine("mine.sql.tmp"), Ct));

        var outside = _folder.Combine("outside.txt");
        await File.WriteAllTextAsync(outside, "secret", Ct);
        var root = _folder.Combine("root");
        Directory.CreateDirectory(root);
        try
        {
            File.CreateSymbolicLink(Path.Combine(root, "x.sql.tmp"), outside);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("Creating a symbolic link needs developer mode or admin rights on Windows.");
        }

        await ScriptFiles.WriteAsync(root, "x.sql", ScriptHeader.Empty, "SELECT 2 FROM dual", ScriptWriteMode.New, null, Ct);

        Assert.Equal("secret", await File.ReadAllTextAsync(outside, Ct));
        Assert.Null(new FileInfo(Path.Combine(root, "x.sql")).LinkTarget);
    }

    /// <summary>Review WP-77, K10: a shared folder that is gone (repository moved) was created anew by a save, empty.</summary>
    [Fact]
    public async Task A_missing_folder_is_created_only_where_allowed()
    {
        var gone = _folder.Combine("moved-repository");

        await Assert.ThrowsAsync<RefusedException>(() =>
            ScriptFiles.WriteAsync(gone, "a.sql", ScriptHeader.Empty, "SELECT 1 FROM dual", ScriptWriteMode.New, null, Ct));
        Assert.False(Directory.Exists(gone));

        await ScriptFiles.WriteAsync(gone, "a.sql", ScriptHeader.Empty, "SELECT 1 FROM dual", ScriptWriteMode.New, null, Ct, createRoot: true);
        Assert.True(File.Exists(Path.Combine(gone, "a.sql")));
    }

    public void Dispose() => _folder.Dispose();
}
