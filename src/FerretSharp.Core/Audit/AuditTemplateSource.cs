using FerretSharp.Core.Resources;
using FerretSharp.Core.Settings;

namespace FerretSharp.Core.Audit;

/// <summary>The template in use, or why there is none.</summary>
/// <param name="Path">The file it comes from; null for the built-in template.</param>
/// <param name="Errors">Why the file cannot be used (not readable, template errors with their lines); empty if it can.</param>
public sealed record AuditTemplateLoad(AuditTemplate? Template, string? Path, IReadOnlyList<string> Errors)
{
    public bool IsDefault => Path is null;
}

/// <summary>
/// The audit template of the settings (WP-26): the built-in one, or the file <see cref="AppSettings.AuditTemplatePath"/>
/// names – read again when it changed on disk, so an edit in the company's repository counts at the next look.
/// </summary>
public sealed class AuditTemplateSource(AppSettingsService settings, TimeProvider? clock = null)
{
    /// <summary>
    /// How long a load is used without looking at the file again: views ask on every render (each keystroke in the
    /// designer), and a file on a share over VPN must not stall the UI thread each time (R5 review).
    /// </summary>
    public static readonly TimeSpan Fresh = TimeSpan.FromSeconds(3);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Lock _lock = new();
    private (string Path, DateTime Written, long Length, AuditTemplateLoad Load, DateTimeOffset Checked)? _cached;

    public AuditTemplateLoad Current() => Load(settings.Current.AuditTemplatePath);

    /// <summary>The template at <paramref name="path"/> (the settings page checks a path before it is saved); empty = built-in.</summary>
    public AuditTemplateLoad Load(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new AuditTemplateLoad(AuditTemplates.Default, null, []);
        }

        path = path.Trim();
        lock (_lock)
        {
            if (_cached is { } recent && recent.Path == path && _clock.GetUtcNow() - recent.Checked < Fresh)
            {
                return recent.Load;
            }
        }

        var load = Read(path, out var written, out var length);
        lock (_lock)
        {
            _cached = (path, written, length, load, _clock.GetUtcNow());
        }

        return load;
    }

    /// <summary>The file, parsed – or the earlier load if the file has not changed since (same time and length).</summary>
    private AuditTemplateLoad Read(string path, out DateTime written, out long length)
    {
        (written, length) = (default, -1);
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists)
            {
                return new AuditTemplateLoad(null, path, [TextFormat.Format(AuditText.TemplateFileMissing, path)]);
            }

            (written, length) = (file.LastWriteTimeUtc, file.Length);
            lock (_lock)
            {
                if (_cached is { } cached && cached.Path == path && cached.Written == written && cached.Length == length)
                {
                    return cached.Load;
                }
            }

            var text = File.ReadAllText(path).ReplaceLineEndings("\n");
            try
            {
                return new AuditTemplateLoad(AuditTemplates.Parse(text), path, []);
            }
            catch (AuditTemplateException ex)
            {
                return new AuditTemplateLoad(null, path, ex.Errors.Select(e => e.ToString()).ToList());
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new AuditTemplateLoad(null, path, [TextFormat.Format(AuditText.TemplateFileUnreadable, path, ex.Message)]);
        }
    }
}
