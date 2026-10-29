using FerretSharp.Core.IO;
using Microsoft.Extensions.Time.Testing;

namespace FerretSharp.Core.Tests.IO;

/// <summary>R5b (review 3.31.0, B7): a file read again only when it changed – the audit template and the display columns file.</summary>
public sealed class CachedFileReadTests : IDisposable
{
    private readonly TestFolder _folder = new();
    private readonly FakeTimeProvider _clock = new();
    private int _reads;

    public void Dispose() => _folder.Dispose();

    private string File1 => _folder.Combine("file.txt");

    /// <summary>Reads the text; a text "locked" plays a file that cannot be read for a moment.</summary>
    private CachedFileRead<string> Cache() => new((_, file) =>
    {
        _reads++;
        if (!file.Exists)
        {
            return new FileRead<string>("missing");
        }

        var text = File.ReadAllText(file.FullName);
        return text == "locked" ? new FileRead<string>("unreadable", Reuse: false) : new FileRead<string>(text);
    }, (_, ex) => "error: " + ex.GetType().Name, _clock);

    private void Write(string text, DateTime written)
    {
        File.WriteAllText(File1, text);
        File.SetLastWriteTimeUtc(File1, written);
    }

    [Fact]
    public void Within_fresh_the_last_result_comes_back_without_looking()
    {
        var cache = Cache();
        Write("eins", new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc));
        Assert.Equal("eins", cache.Get(File1));

        Write("zwei", new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc));

        Assert.Equal("eins", cache.Get(File1));
        Assert.Equal(1, _reads);
    }

    [Fact]
    public void After_fresh_an_unchanged_file_is_not_read_again_a_changed_one_is()
    {
        var cache = Cache();
        Write("eins", new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc));
        cache.Get(File1);

        _clock.Advance(CachedFileRead<string>.Fresh);
        Assert.Equal("eins", cache.Get(File1));
        Assert.Equal(1, _reads);

        _clock.Advance(CachedFileRead<string>.Fresh);
        Write("zwei!", new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc));
        Assert.Equal("zwei!", cache.Get(File1));
        Assert.Equal(2, _reads);
    }

    /// <summary>The audit template kept a failed read until the file changed: a file locked for a moment stayed "unreadable".</summary>
    [Fact]
    public void A_failed_read_is_tried_again_at_the_next_look()
    {
        var cache = Cache();
        var written = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);
        Write("locked", written);
        Assert.Equal("unreadable", cache.Get(File1));

        Write("frei!!", written); // same time; the length is not what decides here
        _clock.Advance(CachedFileRead<string>.Fresh);

        Assert.Equal("frei!!", cache.Get(File1));
    }

    [Fact]
    public void A_missing_file_is_read_too_and_forget_reads_again()
    {
        var cache = Cache();
        Assert.Equal("missing", cache.Get(File1));

        Write("da", new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc));
        cache.Forget();

        Assert.Equal("da", cache.Get(File1));
    }

    /// <summary>WP-51: the audit templates of several groups are asked for in turn – each path keeps its own result.</summary>
    [Fact]
    public void Each_path_is_kept_on_its_own()
    {
        var cache = Cache();
        var other = _folder.Combine("other.txt");
        Write("eins", new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc));
        File.WriteAllText(other, "anders");

        Assert.Equal("eins", cache.Get(File1));
        Assert.Equal("anders", cache.Get(other));
        Assert.Equal("eins", cache.Get(File1));
        Assert.Equal("anders", cache.Get(other));
        Assert.Equal(2, _reads);
    }

    [Fact]
    public void The_path_looked_at_longest_ago_goes_first()
    {
        var cache = Cache();
        for (var i = 0; i <= CachedFileRead<string>.MaxPaths; i++)
        {
            cache.Get(_folder.Combine($"f{i}.txt"));
            _clock.Advance(TimeSpan.FromMilliseconds(1));
        }

        cache.Get(_folder.Combine($"f{CachedFileRead<string>.MaxPaths}.txt"));
        Assert.Equal(CachedFileRead<string>.MaxPaths + 1, _reads);
        cache.Get(_folder.Combine("f0.txt"));
        Assert.Equal(CachedFileRead<string>.MaxPaths + 2, _reads);
    }

    [Fact]
    public void A_path_that_cannot_be_looked_at_is_unreadable()
    {
        Assert.Equal("error: ArgumentException", Cache().Get("bad\0path"));
    }

    /// <summary>
    /// Review 3.54.0, A12: a huge file in the shared repository (an export saved under the wrong name) was read whole on
    /// every change; it is not read, and says why, until it changes.
    /// </summary>
    [Fact]
    public void A_file_beyond_the_limit_is_not_read()
    {
        var cache = Cache();
        using (var file = File.Create(File1))
        {
            file.SetLength(CachedFileRead<string>.MaxBytes + 1);
        }

        Assert.Equal("error: IOException", cache.Get(File1));
        _clock.Advance(CachedFileRead<string>.Fresh);
        Assert.Equal("error: IOException", cache.Get(File1));
        Assert.Equal(0, _reads);
    }}
