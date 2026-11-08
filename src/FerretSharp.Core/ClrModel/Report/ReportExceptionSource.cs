using System.Globalization;
using System.Text.Json;
using FerretSharp.Core.Audit;
using FerretSharp.Core.Connections;
using FerretSharp.Core.IO;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Settings;

namespace FerretSharp.Core.ClrModel;

/// <summary>The exceptions of the report in use and where they come from.</summary>
/// <param name="Path">The file; it need not exist yet (the first exception creates it).</param>
/// <param name="Error">Why the file cannot be used (unreadable, no valid JSON); the exceptions are then empty.</param>
/// <param name="ReadOnly">Why the file is not written although it was read (a newer FerretSharp wrote it); null if it can be.</param>
public sealed record ReportExceptionLoad(ReportExceptions Exceptions, string Path, string? Error, string? ReadOnly = null)
{
    public bool Exists { get; init; }

    /// <summary>The group whose file it is (<see cref="AppSettings.ReportExceptionsGroups"/>); null for the global one.</summary>
    public string? Group { get; init; }

    /// <summary>The path is the one in the data folder (no file set).</summary>
    public bool IsDefault { get; init; }
}

/// <summary>
/// The exceptions file of the report (WP-72, ADR 0042) for a connection: its group's file, else the one of the settings,
/// else <c>report-exceptions.json</c> in the data folder – set like the audit template, so each group of systems can share
/// its own through a repository. Read again when it changed on disk (the report asks while it renders,
/// <see cref="CachedFileRead{T}"/>); adding or removing an exception reads the file again first and changes only that entry,
/// so changes of others are kept. Same rules as the display columns file (ADR 0022): invalid JSON is reported and never
/// overwritten, a file of a newer version only read.
/// </summary>
public sealed class ReportExceptionSource(AppSettingsService settings, string defaultPath, TimeProvider? clock = null)
{
    private readonly CachedFileRead<ReportExceptionLoad> _file = new(Read, Unreadable, clock);
    private readonly SemaphoreSlim _writing = new(1, 1);

    /// <summary>The exceptions for <paramref name="profile"/>; null for no connection (the global or default file).</summary>
    public ReportExceptionLoad For(ConnectionProfile? profile)
    {
        var current = settings.Current;
        if (AuditTemplateSource.GroupEntry(current.ReportExceptionsGroups, profile?.Group) is { } entry)
        {
            return Load(entry.Value) with { Group = entry.Key };
        }

        return Load(current.ReportExceptionsPath);
    }

    /// <summary>The file at <paramref name="path"/> (the settings page checks a path before it is saved); empty = data folder.</summary>
    public ReportExceptionLoad Load(string? path) =>
        string.IsNullOrWhiteSpace(path) ? _file.Get(defaultPath) with { IsDefault = true } : _file.Get(path.Trim());

    /// <summary>Adds or replaces the exception for a table name or pattern in the file of <paramref name="profile"/>.</summary>
    /// <exception cref="RefusedException">The file cannot be read, is no valid JSON, comes from a newer FerretSharp or cannot be written.</exception>
    public Task SetAsync(ConnectionProfile? profile, string tableOrPattern, string? reason, CancellationToken cancellationToken) =>
        ChangeAsync(profile, document => document.Set(tableOrPattern, reason), cancellationToken);

    /// <summary>Removes the exception written exactly so (case ignored); as <see cref="SetAsync"/>.</summary>
    /// <exception cref="RefusedException">As for <see cref="SetAsync"/>.</exception>
    public Task RemoveAsync(ConnectionProfile? profile, string tableOrPattern, CancellationToken cancellationToken) =>
        ChangeAsync(profile, document => document.Remove(tableOrPattern), cancellationToken);

    private async Task ChangeAsync(ConnectionProfile? profile, Action<ReportExceptionDocument> change, CancellationToken cancellationToken)
    {
        await _writing.WaitAsync(cancellationToken);
        try
        {
            var path = For(profile).Path;
            ReportExceptionDocument document;
            try
            {
                // One read: the document written back is the one the change is based on.
                document = await Task.Run(() => ReadDocument(path), cancellationToken) ?? new ReportExceptionDocument();
            }
            catch (JsonException ex)
            {
                throw new RefusedException(TextFormat.Format(ClrModelText.ReportExceptionsInvalid, path, ex.Message), ex);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                throw new RefusedException(TextFormat.Format(ClrModelText.ReportExceptionsUnreadable, path, ex.Message), ex);
            }

            if (NewerVersion(path, document) is { } readOnly)
            {
                throw new RefusedException(readOnly);
            }

            change(document);
            try
            {
                // The file may lie in the company's repository: never through a file of a fixed temporary name (review WP-77).
                await AtomicJsonFile.WriteInUserFolderAsync(path, document, ReportExceptionDocument.Options, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                throw new RefusedException(TextFormat.Format(ClrModelText.ReportExceptionsNotWritten, path, ex.Message), ex);
            }
        }
        finally
        {
            _writing.Release();
        }

        _file.Forget();
    }

    /// <summary>The file, parsed; only a successful read is reused while the file is unchanged.</summary>
    private static FileRead<ReportExceptionLoad> Read(string path, FileInfo file)
    {
        if (!file.Exists)
        {
            return new(new ReportExceptionLoad(ReportExceptions.Empty, path, null));
        }

        try
        {
            var document = ReadDocument(path) ?? new ReportExceptionDocument();
            return new(new ReportExceptionLoad(document.Exceptions(), path, null, NewerVersion(path, document)) { Exists = true });
        }
        catch (JsonException ex)
        {
            return new(new ReportExceptionLoad(ReportExceptions.Empty, path, TextFormat.Format(ClrModelText.ReportExceptionsInvalid, path, ex.Message)) { Exists = true }, Reuse: false);
        }
    }

    private static ReportExceptionLoad Unreadable(string path, Exception ex) =>
        new(ReportExceptions.Empty, path, TextFormat.Format(ClrModelText.ReportExceptionsUnreadable, path, ex.Message));

    private static string? NewerVersion(string path, ReportExceptionDocument document) =>
        document.Version > ReportExceptionDocument.CurrentVersion
            ? TextFormat.Format(ClrModelText.ReportExceptionsNewerVersion, path, document.Version.ToString(CultureInfo.InvariantCulture))
            : null;

    /// <summary>The document; null for a missing or empty file.</summary>
    /// <exception cref="JsonException">No valid JSON (or not the expected shape).</exception>
    /// <exception cref="IOException">Also for a file larger than <see cref="CachedFileRead{T}.MaxBytes"/>.</exception>
    private static ReportExceptionDocument? ReadDocument(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists)
        {
            return null;
        }

        if (file.Length > CachedFileRead<ReportExceptionLoad>.MaxBytes)
        {
            throw CachedFileRead<ReportExceptionLoad>.TooLarge();
        }

        var text = File.ReadAllText(path);
        return string.IsNullOrWhiteSpace(text) ? null : JsonSerializer.Deserialize<ReportExceptionDocument>(text, ReportExceptionDocument.Options);
    }
}
