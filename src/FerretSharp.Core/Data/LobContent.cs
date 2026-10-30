using System.Globalization;
using System.Text;
using System.Text.Json;
using FerretSharp.Core.Resources;

namespace FerretSharp.Core.Data;

/// <summary>
/// Helpers of the LOB editor (WP-10): hex view of a BLOB, file type by content, text files to CLOB text, JSON or XML
/// in a CLOB (colouring, indenting).
/// </summary>
public static class LobContent
{
    private const int BytesPerLine = 16;

    /// <summary>Kind of binary content, recognized by its first bytes (magic numbers).</summary>
    /// <param name="Extension">File extension without dot, e.g. <c>png</c>; <c>bin</c> if unknown.</param>
    /// <param name="ImageMime">MIME type if the browser can show it as an image; null otherwise.</param>
    public sealed record BinaryKind(string Extension, string? ImageMime);

    private static readonly (byte[] Magic, BinaryKind Kind)[] Signatures =
    [
        ([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], new("png", "image/png")),
        ([0xFF, 0xD8, 0xFF], new("jpg", "image/jpeg")),
        ("GIF8"u8.ToArray(), new("gif", "image/gif")),
        ("BM"u8.ToArray(), new("bmp", "image/bmp")),
        ("%PDF"u8.ToArray(), new("pdf", null)),
        ([0x50, 0x4B, 0x03, 0x04], new("zip", null)), // also docx, xlsx …
        ([0x1F, 0x8B], new("gz", null)),
    ];

    public static BinaryKind Detect(ReadOnlySpan<byte> content)
    {
        foreach (var (magic, kind) in Signatures)
        {
            if (content.StartsWith(magic))
            {
                return kind;
            }
        }

        // WEBP: "RIFF" size "WEBP"
        return content.Length >= 12 && content[..4].SequenceEqual("RIFF"u8) && content[8..12].SequenceEqual("WEBP"u8)
            ? new BinaryKind("webp", "image/webp")
            : new BinaryKind("bin", null);
    }

    /// <summary>
    /// Classic hex view of the first <paramref name="maxBytes"/> bytes: offset, 16 bytes in hex, the printable ASCII
    /// characters (others as '.').
    /// </summary>
    public static string HexDump(ReadOnlySpan<byte> content, int maxBytes)
    {
        var shown = content[..Math.Min(content.Length, maxBytes)];
        var builder = new StringBuilder();
        for (var offset = 0; offset < shown.Length; offset += BytesPerLine)
        {
            var line = shown.Slice(offset, Math.Min(BytesPerLine, shown.Length - offset));
            builder.Append(offset.ToString("X8", CultureInfo.InvariantCulture)).Append("  ");
            for (var i = 0; i < BytesPerLine; i++)
            {
                builder.Append(i < line.Length ? line[i].ToString("X2", CultureInfo.InvariantCulture) + " " : "   ");
                if (i == 7)
                {
                    builder.Append(' ');
                }
            }

            builder.Append(' ');
            foreach (var b in line)
            {
                builder.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
            }

            builder.Append('\n');
        }

        return builder.ToString().TrimEnd('\n');
    }

    /// <summary>
    /// A text file as CLOB text: UTF-8 (with or without BOM) or UTF-16 with BOM; anything that is not valid UTF-8 is
    /// read as Windows-1252/Latin-1 (old Windows files), which <paramref name="utf8"/> reports.
    /// </summary>
    public static string DecodeText(byte[] content, out bool utf8)
    {
        utf8 = true;
        if (content.AsSpan().StartsWith(Encoding.UTF8.Preamble))
        {
            return Encoding.UTF8.GetString(content, 3, content.Length - 3);
        }

        if (content.AsSpan().StartsWith(Encoding.Unicode.Preamble))
        {
            return Encoding.Unicode.GetString(content, 2, content.Length - 2);
        }

        if (content.AsSpan().StartsWith(Encoding.BigEndianUnicode.Preamble))
        {
            return Encoding.BigEndianUnicode.GetString(content, 2, content.Length - 2);
        }

        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(content);
        }
        catch (DecoderFallbackException)
        {
            utf8 = false;
            return Encoding.Latin1.GetString(content);
        }
    }

    /// <summary>
    /// What a CLOB holds, by its first character after leading white space: <c>{</c> or <c>[</c> JSON, <c>&lt;</c> XML,
    /// anything else text. Only picks the colouring – whether the JSON is valid shows when it is indented.
    /// </summary>
    public static LobTextFormat DetectFormat(string text)
    {
        foreach (var c in text)
        {
            if (!char.IsWhiteSpace(c))
            {
                return c switch
                {
                    '{' or '[' => LobTextFormat.Json,
                    '<' => LobTextFormat.Xml,
                    _ => LobTextFormat.Text,
                };
            }
        }

        return LobTextFormat.Text;
    }

    /// <summary>
    /// Valid JSON indented by two spaces per level, one member or element per line. Strings and numbers stay exactly as
    /// written (no re-escaping, no number formatting) – only the white space between the tokens changes. Invalid JSON
    /// (also with comments or trailing commas) gives the position of the first error instead.
    /// </summary>
    public static JsonIndentResult IndentJson(string text)
    {
        try
        {
            using var _ = JsonDocument.Parse(text);
        }
        catch (JsonException ex)
        {
            return new JsonIndentResult(null, (ex.LineNumber ?? 0) + 1, (ex.BytePositionInLine ?? 0) + 1);
        }

        var builder = new StringBuilder(text.Length + text.Length / 4);
        var depth = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            switch (c)
            {
                case '"':
                    var end = StringEnd(text, i);
                    builder.Append(text, i, end - i + 1);
                    i = end;
                    break;
                case '{' or '[':
                    var next = NextToken(text, i + 1);
                    if (next < text.Length && text[next] == (c == '{' ? '}' : ']'))
                    {
                        builder.Append(c).Append(text[next]); // {} and [] stay on one line
                        i = next;
                    }
                    else
                    {
                        builder.Append(c);
                        NewLine(builder, ++depth);
                    }

                    break;
                case '}' or ']':
                    NewLine(builder, --depth);
                    builder.Append(c);
                    break;
                case ',':
                    builder.Append(c);
                    NewLine(builder, depth);
                    break;
                case ':':
                    builder.Append(": ");
                    break;
                default:
                    if (!char.IsWhiteSpace(c))
                    {
                        builder.Append(c);
                    }

                    break;
            }
        }

        return new JsonIndentResult(builder.ToString(), null, null);
    }

    /// <summary>Index of the quote that ends the string starting at <paramref name="start"/> (valid JSON: there is one).</summary>
    private static int StringEnd(string text, int start)
    {
        for (var i = start + 1; i < text.Length; i++)
        {
            if (text[i] == '\\')
            {
                i++;
            }
            else if (text[i] == '"')
            {
                return i;
            }
        }

        return text.Length - 1;
    }

    private static int NextToken(string text, int from)
    {
        while (from < text.Length && char.IsWhiteSpace(text[from]))
        {
            from++;
        }

        return from;
    }

    private static void NewLine(StringBuilder builder, int depth) => builder.Append('\n').Append(' ', depth * 2);

    /// <summary>"12.345 characters" / "1,2 MB" for the dialog.</summary>
    public static string Describe(object? value) => value switch
    {
        null => "NULL",
        string text => CharacterCount(text.Length),
        byte[] bytes => Size(bytes.LongLength),
        _ => "",
    };

    public static string Size(long bytes) => bytes switch
    {
        < 1024 => ByteCount(bytes),
        < 1024 * 1024 => $"{(bytes / 1024.0).ToString("N1", ValueCulture.German)} KB",
        _ => $"{(bytes / 1024.0 / 1024.0).ToString("N1", ValueCulture.German)} MB",
    };

    /// <summary>"12.345 characters" (German number notation like the rest of the cell texts).</summary>
    public static string CharacterCount(long count) =>
        TextFormat.Plural(ValueCulture.German, count, DataText.CharacterCountOne, DataText.CharacterCountOther);

    /// <summary>"2.048 bytes" (German number notation like the rest of the cell texts).</summary>
    public static string ByteCount(long count) =>
        TextFormat.Plural(ValueCulture.German, count, DataText.ByteCountOne, DataText.ByteCountOther);
}

/// <summary>What a CLOB holds, for the colouring in the LOB editor.</summary>
public enum LobTextFormat
{
    Text,
    Json,
    Xml,
}

/// <summary>Result of <see cref="LobContent.IndentJson"/>: the indented text, or where the JSON is invalid (1-based).</summary>
/// <param name="Column">Position in the line counted in UTF-8 bytes (as <see cref="JsonException"/> reports it); the
/// same as the character for ASCII.</param>
public sealed record JsonIndentResult(string? Text, long? Line, long? Column);
