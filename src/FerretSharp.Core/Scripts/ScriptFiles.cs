using System.Security.Cryptography;
using System.Text;
using FerretSharp.Core.Connections;
using FerretSharp.Core.IO;
using FerretSharp.Core.Resources;

namespace FerretSharp.Core.Scripts;

/// <summary>A script as read from its file.</summary>
/// <param name="FileHash">The file's bytes as read (<see cref="ScriptFiles.HashOf(byte[])"/>): a save checks against it whether someone changed the file since.</param>
public sealed record ScriptDocument(ParsedScript Script, string FileHash);

/// <summary>How a save treats a file already there.</summary>
public enum ScriptWriteMode
{
    /// <summary>A new script: an existing file is not replaced (<see cref="ScriptWriteOutcome.Exists"/>).</summary>
    New,

    /// <summary>The file the tab was opened from: replaced only if unchanged since (<see cref="ScriptWriteOutcome.ChangedOnDisk"/>).</summary>
    Update,

    /// <summary>Replaced whatever it holds (the user confirmed).</summary>
    Replace,
}

public enum ScriptWriteOutcome
{
    Written,

    /// <summary>The file exists (<see cref="ScriptWriteMode.New"/>): nothing written.</summary>
    Exists,

    /// <summary>The file changed since it was read, or is gone (<see cref="ScriptWriteMode.Update"/>): nothing written.</summary>
    ChangedOnDisk,
}

/// <param name="FileHash">The bytes written (<see cref="ScriptWriteOutcome.Written"/>), else null.</param>
public sealed record ScriptWrite(ScriptWriteOutcome Outcome, string? FileHash);

/// <summary>
/// Reads, writes and deletes script files (WP-77), always through <see cref="ScriptPaths.Resolve"/>. Writing is atomic
/// (<see cref="AtomicFile"/>) and keeps what a file has: its encoding with or without BOM and its line breaks – a file in a
/// shared repository must not show as changed in every line. New files are UTF-8 without BOM with CRLF (decision of the
/// user, 2026-11-08).
/// </summary>
public static class ScriptFiles
{
    /// <summary>A larger file is not read: a script is a few kilobytes, and Monaco shows it whole.</summary>
    public const long MaxBytes = 1024 * 1024;

    private const string NewFileLineBreak = "\r\n";

    private static readonly Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Reads and splits the script.</summary>
    /// <exception cref="RefusedException">The path is refused, the file is missing, too large or cannot be read.</exception>
    public static Task<ScriptDocument> ReadAsync(string root, string relative, CancellationToken cancellationToken) =>
        Task.Run(() => ReadCoreAsync(root, relative, cancellationToken), cancellationToken); // off the UI thread: a share over VPN

    private static async Task<ScriptDocument> ReadCoreAsync(string root, string relative, CancellationToken cancellationToken)
    {
        var path = ScriptPaths.Resolve(root, relative);
        var bytes = await ReadBytesAsync(path, relative, cancellationToken)
                    ?? throw new RefusedException(TextFormat.Format(ScriptText.FileMissing, relative));
        var (text, _) = Decode(bytes);
        return new ScriptDocument(ScriptHeaders.Parse(text), HashOf(bytes));
    }

    /// <summary>The hash of the file's bytes now; null if it does not exist.</summary>
    /// <exception cref="RefusedException">The path is refused, the file is too large or cannot be read.</exception>
    public static Task<string?> FileHashAsync(string root, string relative, CancellationToken cancellationToken) =>
        Task.Run(async () =>
        {
            var path = ScriptPaths.Resolve(root, relative);
            return await ReadBytesAsync(path, relative, cancellationToken) is { } bytes ? HashOf(bytes) : null;
        }, cancellationToken);

    /// <summary>
    /// Writes the script: header and body (<see cref="ScriptHeaders.Compose"/>) in the file's encoding and line breaks.
    /// Creates missing folders below the root.
    /// </summary>
    /// <param name="expectedHash">For <see cref="ScriptWriteMode.Update"/>: the file as read when the tab opened it.</param>
    /// <exception cref="RefusedException">The path is refused, the text does not fit the file's encoding, or writing failed.</exception>
    public static Task<ScriptWrite> WriteAsync(
        string root, string relative, ScriptHeader header, string body, ScriptWriteMode mode, string? expectedHash, CancellationToken cancellationToken) =>
        Task.Run(() => WriteCoreAsync(root, relative, header, body, mode, expectedHash, cancellationToken), cancellationToken);

    private static async Task<ScriptWrite> WriteCoreAsync(
        string root, string relative, ScriptHeader header, string body, ScriptWriteMode mode, string? expectedHash, CancellationToken cancellationToken)
    {
        var path = ScriptPaths.Resolve(root, relative);
        var existing = await ReadBytesAsync(path, relative, cancellationToken);
        switch (mode)
        {
            case ScriptWriteMode.New when existing is not null:
                return new ScriptWrite(ScriptWriteOutcome.Exists, null);
            case ScriptWriteMode.Update when existing is null || HashOf(existing) != expectedHash:
                return new ScriptWrite(ScriptWriteOutcome.ChangedOnDisk, null);
        }

        var (encoding, preamble, lineBreak) = existing is null ? (StrictUtf8, Array.Empty<byte>(), NewFileLineBreak) : FormatOf(existing);
        var text = ScriptHeaders.Compose(header, body).Replace("\n", lineBreak, StringComparison.Ordinal);
        byte[] bytes;
        try
        {
            bytes = [.. preamble, .. encoding.GetBytes(text)];
        }
        catch (EncoderFallbackException ex)
        {
            throw new RefusedException(TextFormat.Format(ScriptText.EncodingCannotHold, relative, encoding.WebName), ex);
        }

        try
        {
            await AtomicFile.WriteAsync(path, async (stream, ct) =>
            {
                await stream.WriteAsync(bytes, ct);
                return true;
            }, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new RefusedException(TextFormat.Format(ScriptText.FileNotWritten, relative, ex.Message), ex);
        }

        return new ScriptWrite(ScriptWriteOutcome.Written, HashOf(bytes));
    }

    /// <summary>Deletes the script's file (not its folder); a file already gone is nothing to do.</summary>
    /// <exception cref="RefusedException">The path is refused or the file cannot be deleted.</exception>
    public static Task DeleteAsync(string root, string relative, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            var path = ScriptPaths.Resolve(root, relative);
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new RefusedException(TextFormat.Format(ScriptText.FileNotDeleted, relative, ex.Message), ex);
            }
        }, cancellationToken);

    /// <summary>The hash of a file's bytes (SHA-256, hex).</summary>
    public static string HashOf(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    /// <summary>The hash of a text with its line breaks as <c>\n</c> – what a tab compares to see whether it was changed.</summary>
    public static string HashOf(string text) => HashOf(Encoding.UTF8.GetBytes(text.ReplaceLineEndings("\n")));

    /// <summary>The text of a file's bytes: by its BOM, else UTF-8 if it is valid UTF-8, else Windows-1252.</summary>
    public static (string Text, Encoding Encoding) Decode(byte[] bytes)
    {
        var (encoding, preamble, _) = FormatOf(bytes);
        return (encoding.GetString(bytes, preamble.Length, bytes.Length - preamble.Length), encoding);
    }

    private static (Encoding Encoding, byte[] Preamble, string LineBreak) FormatOf(byte[] bytes)
    {
        Encoding encoding;
        if (bytes is [0xEF, 0xBB, 0xBF, ..])
        {
            encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true, throwOnInvalidBytes: false);
        }
        else if (bytes is [0xFF, 0xFE, ..])
        {
            encoding = new UnicodeEncoding(bigEndian: false, byteOrderMark: true);
        }
        else if (bytes is [0xFE, 0xFF, ..])
        {
            encoding = new UnicodeEncoding(bigEndian: true, byteOrderMark: true);
        }
        else
        {
            encoding = IsUtf8(bytes) ? StrictUtf8 : Windows1252();
        }

        var preamble = encoding.GetPreamble();
        var text = encoding.GetString(bytes, preamble.Length, bytes.Length - preamble.Length);
        var lineBreak = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : text.Contains('\n', StringComparison.Ordinal) ? "\n" : NewFileLineBreak;
        return (encoding, preamble, lineBreak);
    }

    private static bool IsUtf8(byte[] bytes)
    {
        try
        {
            StrictUtf8.GetCharCount(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    /// <summary>An older file written as ANSI (German Windows); a character it cannot hold is refused on save, not replaced by "?".</summary>
    private static readonly Lazy<Encoding> Ansi = new(() =>
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1252, EncoderFallback.ExceptionFallback, DecoderFallback.ReplacementFallback);
    });

    private static Encoding Windows1252() => Ansi.Value;

    private static async Task<byte[]?> ReadBytesAsync(string path, string relative, CancellationToken cancellationToken)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists)
            {
                return null;
            }

            if (file.Length > MaxBytes)
            {
                throw new RefusedException(TextFormat.Format(ScriptText.FileTooLarge, relative, MaxBytes / 1024));
            }

            return await File.ReadAllBytesAsync(path, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new RefusedException(TextFormat.Format(ScriptText.FileUnreadable, relative, ex.Message), ex);
        }
    }
}
