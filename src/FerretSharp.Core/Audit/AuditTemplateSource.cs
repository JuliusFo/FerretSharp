using FerretSharp.Core.IO;
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
    public static readonly TimeSpan Fresh = CachedFileRead<AuditTemplateLoad>.Fresh;

    private readonly CachedFileRead<AuditTemplateLoad> _file = new(Read, Unreadable, clock);

    public AuditTemplateLoad Current() => Load(settings.Current.AuditTemplatePath);

    /// <summary>The template at <paramref name="path"/> (the settings page checks a path before it is saved); empty = built-in.</summary>
    public AuditTemplateLoad Load(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new AuditTemplateLoad(AuditTemplates.Default, null, []);
        }

        return _file.Get(path.Trim());
    }

    /// <summary>The file, parsed; template errors are kept while the file is unchanged, a failed read is tried again.</summary>
    private static FileRead<AuditTemplateLoad> Read(string path, FileInfo file)
    {
        if (!file.Exists)
        {
            return new(new AuditTemplateLoad(null, path, [TextFormat.Format(AuditText.TemplateFileMissing, path)]));
        }

        var text = File.ReadAllText(path).ReplaceLineEndings("\n");
        try
        {
            return new(new AuditTemplateLoad(AuditTemplates.Parse(text), path, []));
        }
        catch (AuditTemplateException ex)
        {
            return new(new AuditTemplateLoad(null, path, ex.Errors.Select(e => e.ToString()).ToList()));
        }
    }

    private static AuditTemplateLoad Unreadable(string path, Exception ex) =>
        new(null, path, [TextFormat.Format(AuditText.TemplateFileUnreadable, path, ex.Message)]);
}
