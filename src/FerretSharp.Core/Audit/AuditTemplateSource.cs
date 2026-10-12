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
public sealed class AuditTemplateSource(AppSettingsService settings)
{
    private readonly Lock _lock = new();
    private (string Path, DateTime Written, long Length, AuditTemplateLoad Load)? _cached;

    public AuditTemplateLoad Current() => Load(settings.Current.AuditTemplatePath);

    /// <summary>The template at <paramref name="path"/> (the settings page checks a path before it is saved); empty = built-in.</summary>
    public AuditTemplateLoad Load(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new AuditTemplateLoad(AuditTemplates.Default, null, []);
        }

        path = path.Trim();
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists)
            {
                return new AuditTemplateLoad(null, path, [TextFormat.Format(AuditText.TemplateFileMissing, path)]);
            }

            lock (_lock)
            {
                if (_cached is { } cached && cached.Path == path && cached.Written == file.LastWriteTimeUtc && cached.Length == file.Length)
                {
                    return cached.Load;
                }
            }

            var text = File.ReadAllText(path).ReplaceLineEndings("\n");
            AuditTemplateLoad load;
            try
            {
                load = new AuditTemplateLoad(AuditTemplates.Parse(text), path, []);
            }
            catch (AuditTemplateException ex)
            {
                load = new AuditTemplateLoad(null, path, ex.Errors.Select(e => e.ToString()).ToList());
            }

            lock (_lock)
            {
                _cached = (path, file.LastWriteTimeUtc, file.Length, load);
            }

            return load;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new AuditTemplateLoad(null, path, [TextFormat.Format(AuditText.TemplateFileUnreadable, path, ex.Message)]);
        }
    }
}
