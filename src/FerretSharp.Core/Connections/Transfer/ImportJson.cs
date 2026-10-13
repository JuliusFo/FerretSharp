using System.IO.Compression;
using System.Text.Json;
using FerretSharp.Core.Resources;

namespace FerretSharp.Core.Connections.Transfer;

/// <summary>Reading foreign JSON leniently: missing or oddly typed values are null, never an exception.</summary>
internal static class ImportJson
{
    private static readonly JsonDocumentOptions Options = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    public static JsonDocument Parse(byte[] json)
    {
        try
        {
            return JsonDocument.Parse(json, Options);
        }
        catch (JsonException ex)
        {
            throw new ConnectionImportException(TextFormat.Format(ConnectionText.ImportInvalidJson, ex.Message), ex);
        }
    }

    /// <summary>A string value (numbers and booleans as their text); null if missing, empty or an object/array.</summary>
    public static string? Text(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value))
        {
            return null;
        }

        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
            _ => null,
        };
        return string.IsNullOrEmpty(text) ? null : text;
    }
}

/// <summary>How much a file to import may be: connection files are a few KB, a project export maybe a few hundred.</summary>
internal static class ImportLimits
{
    public const long MaxFileBytes = 16 * 1024 * 1024;
    public const int MaxZipEntries = 10_000;

    /// <summary>One zip entry, refusing anything larger than <see cref="MaxFileBytes"/> (whatever the header claims).</summary>
    public static byte[] ReadEntry(ZipArchiveEntry entry)
    {
        if (entry.Length > MaxFileBytes)
        {
            throw new ConnectionImportException(ConnectionText.ImportFileTooLarge);
        }

        using var stream = entry.Open();
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = stream.Read(chunk)) > 0)
        {
            if (buffer.Length + read > MaxFileBytes)
            {
                throw new ConnectionImportException(ConnectionText.ImportFileTooLarge);
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>A file from disk, refusing anything larger than <see cref="MaxFileBytes"/>.</summary>
    public static async Task<byte[]> ReadFileAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            RefuseLarge(path);
            return await File.ReadAllBytesAsync(path, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ConnectionImportException(TextFormat.Format(ConnectionText.ImportUnreadable, ex.Message), ex);
        }
    }

    /// <inheritdoc cref="ReadFileAsync"/>
    public static byte[] ReadFile(string path)
    {
        try
        {
            RefuseLarge(path);
            return File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ConnectionImportException(TextFormat.Format(ConnectionText.ImportUnreadable, ex.Message), ex);
        }
    }

    private static void RefuseLarge(string path)
    {
        if (new FileInfo(path).Length > MaxFileBytes)
        {
            throw new ConnectionImportException(ConnectionText.ImportFileTooLarge);
        }
    }
}
