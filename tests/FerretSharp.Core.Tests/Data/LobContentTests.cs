using System.Text;
using FerretSharp.Core.Data;

namespace FerretSharp.Core.Tests.Data;

public class LobContentTests
{
    [Fact]
    public void Binary_content_is_recognized_by_its_first_bytes()
    {
        Assert.Equal(new LobContent.BinaryKind("png", "image/png"), LobContent.Detect([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0]));
        Assert.Equal("image/jpeg", LobContent.Detect([0xFF, 0xD8, 0xFF, 0xE0]).ImageMime);
        Assert.Equal("pdf", LobContent.Detect("%PDF-1.7"u8).Extension);
        Assert.Equal("webp", LobContent.Detect("RIFF\0\0\0\0WEBPVP8 "u8).Extension);
        Assert.Equal(new LobContent.BinaryKind("bin", null), LobContent.Detect([1, 2, 3]));
        Assert.Equal("bin", LobContent.Detect([]).Extension);
    }

    [Fact]
    public void Hex_dump_shows_offset_bytes_and_printable_characters()
    {
        var bytes = "Hallo, Welt! 0123"u8.ToArray();

        var dump = LobContent.HexDump(bytes, 1024);

        Assert.Equal(
            "00000000  48 61 6C 6C 6F 2C 20 57  65 6C 74 21 20 30 31 32  Hallo, Welt! 012\n"
            + "00000010  33                                                3",
            dump);
        Assert.Single(LobContent.HexDump(new byte[100], 16).Split('\n'));
    }

    [Fact]
    public void Text_files_are_read_as_utf8_utf16_or_latin1()
    {
        Assert.Equal("Grüße", LobContent.DecodeText(Encoding.UTF8.GetBytes("Grüße"), out var utf8));
        Assert.True(utf8);
        Assert.Equal("Grüße", LobContent.DecodeText([.. Encoding.UTF8.Preamble, .. Encoding.UTF8.GetBytes("Grüße")], out _));
        Assert.Equal("Grüße", LobContent.DecodeText([.. Encoding.Unicode.Preamble, .. Encoding.Unicode.GetBytes("Grüße")], out _));

        Assert.Equal("Grüße", LobContent.DecodeText(Encoding.Latin1.GetBytes("Grüße"), out utf8));
        Assert.False(utf8);
    }

    [Fact]
    public void Sizes_and_lengths_read_german()
    {
        Assert.Equal("12.345 Zeichen", LobContent.Describe(new string('x', 12345)));
        Assert.Equal("512 Bytes", LobContent.Describe(new byte[512]));
        Assert.Equal("1,5 KB", LobContent.Size(1536));
        Assert.Equal("2,0 MB", LobContent.Size(2 * 1024 * 1024));
        Assert.Equal("NULL", LobContent.Describe(null));
    }
}
