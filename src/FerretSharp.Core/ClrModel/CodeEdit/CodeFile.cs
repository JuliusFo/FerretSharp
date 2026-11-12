using System.Security.Cryptography;
using System.Text;
using FerretSharp.Core.IO;

namespace FerretSharp.Core.ClrModel;

/// <summary>The byte order mark a source file starts with; its text is written back with the same one (WP-41).</summary>
public enum CodeFileBom
{
    None,
    Utf8,
    Utf16LittleEndian,
    Utf16BigEndian,
}

/// <summary>
/// How a source file is written, so that a change keeps it (WP-41, decision of the user): its byte order mark, its line
/// ending and the unit it indents by. A file without byte order mark is UTF-8 – one that is not valid UTF-8 is not read.
/// </summary>
/// <param name="Indent">One level of indentation: four spaces, a tab …</param>
public sealed record CodeFileFormat(CodeFileBom Bom, string NewLine, string Indent)
{
    /// <summary>For a file written new without a neighbour to learn from.</summary>
    public static readonly CodeFileFormat Default = new(CodeFileBom.Utf8, "\r\n", "    ");

    /// <summary>The text as the file's bytes, with the byte order mark.</summary>
    public byte[] Encode(string text)
    {
        var (encoding, preamble) = Bom switch
        {
            CodeFileBom.Utf16LittleEndian => ((Encoding)new UnicodeEncoding(bigEndian: false, byteOrderMark: true), true),
            CodeFileBom.Utf16BigEndian => (new UnicodeEncoding(bigEndian: true, byteOrderMark: true), true),
            CodeFileBom.Utf8 => (new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), true),
            _ => (new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), false),
        };
        var body = encoding.GetBytes(text);
        return preamble ? [.. encoding.GetPreamble(), .. body] : body;
    }

    /// <summary><paramref name="text"/> (written with <c>\n</c>) with the file's line ending and indentation unit.</summary>
    /// <remarks>Leading groups of four spaces become the file's unit; other spaces stay.</remarks>
    public string Apply(string text)
    {
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        if (Indent != "    ")
        {
            for (var i = 0; i < lines.Length; i++)
            {
                var spaces = lines[i].Length - lines[i].TrimStart(' ').Length;
                var levels = spaces / 4;
                lines[i] = string.Concat(Enumerable.Repeat(Indent, levels)) + lines[i][(levels * 4)..];
            }
        }

        return string.Join(NewLine, lines);
    }
}

/// <summary>
/// A C# file of the user's project as read for a change (WP-41): its text, how it is written and a hash of its bytes – a
/// change is written only to a file that still has them (decision of the user: unchanged since the analysis).
/// </summary>
public sealed class CodeFile
{
    /// <summary>A larger source file is not read: generated code, not an entity or its configuration.</summary>
    public const int MaxBytes = 2 * 1024 * 1024;

    private CodeFile(string path, string text, CodeFileFormat format, byte[] hash)
    {
        Path = path;
        Text = text;
        Format = format;
        Hash = hash;
    }

    public string Path { get; }

    /// <summary>The text without byte order mark, line endings as in the file.</summary>
    public string Text { get; }

    public CodeFileFormat Format { get; }

    /// <summary>SHA-256 of the file's bytes as read.</summary>
    public byte[] Hash { get; }

    /// <summary>The file; null if it is larger than <see cref="MaxBytes"/> or not UTF-8 or UTF-16 text.</summary>
    /// <exception cref="IOException">It cannot be read.</exception>
    /// <exception cref="UnauthorizedAccessException">It may not be read.</exception>
    public static CodeFile? Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > MaxBytes)
        {
            return null;
        }

        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return FromBytes(path, bytes);
    }

    /// <summary>The file from its bytes; null if they are not UTF-8 or UTF-16 text.</summary>
    public static CodeFile? FromBytes(string path, byte[] bytes)
    {
        var (bom, skip) = bytes switch
        {
            [0xEF, 0xBB, 0xBF, ..] => (CodeFileBom.Utf8, 3),
            [0xFF, 0xFE, ..] => (CodeFileBom.Utf16LittleEndian, 2),
            [0xFE, 0xFF, ..] => (CodeFileBom.Utf16BigEndian, 2),
            _ => (CodeFileBom.None, 0),
        };
        Encoding strict = bom switch
        {
            CodeFileBom.Utf16LittleEndian => new UnicodeEncoding(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true),
            CodeFileBom.Utf16BigEndian => new UnicodeEncoding(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true),
            _ => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
        };

        string text;
        try
        {
            text = strict.GetString(bytes, skip, bytes.Length - skip);
        }
        catch (DecoderFallbackException)
        {
            return null; // ANSI (Windows-1252) or binary: written back as UTF-8 it would change every umlaut
        }

        if (text.Contains('\0', StringComparison.Ordinal))
        {
            return null;
        }

        return new CodeFile(path, text, new CodeFileFormat(bom, TextFileFormat.LineBreakOf(text), IndentOf(text)), SHA256.HashData(bytes));
    }

    /// <summary>
    /// The indentation unit: a tab if lines start with tabs, else the step most often found between the indentations of
    /// lines that follow each other (2 to 8 spaces; a <c> *</c> line of a block comment is one); four spaces without one.
    /// </summary>
    internal static string IndentOf(string text)
    {
        var (tabs, spaced) = (0, 0);
        var steps = new int[9];
        var previous = 0;
        foreach (var line in text.ReplaceLineEndings("\n").Split('\n'))
        {
            if (line.Trim().Length == 0)
            {
                continue;
            }

            if (line[0] == '\t')
            {
                tabs++;
                continue;
            }

            var spaces = line.Length - line.TrimStart(' ').Length;
            if (spaces > 0)
            {
                spaced++;
            }

            if (spaces - previous is >= 2 and <= 8 and var step)
            {
                steps[step]++;
            }

            previous = spaces;
        }

        if (tabs > spaced)
        {
            return "\t";
        }

        var best = 4;
        for (var step = 2; step <= 8; step++)
        {
            if (steps[step] > steps[best])
            {
                best = step;
            }
        }

        return new string(' ', best);
    }
}
