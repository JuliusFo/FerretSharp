using FerretSharp.Core.Connections;
using FerretSharp.Core.IO;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Settings;

namespace FerretSharp.Core.Audit;

/// <summary>Where the template of a connection is set (WP-51), from the most specific.</summary>
public enum AuditTemplateOrigin
{
    /// <summary>Nothing is set: the built-in template.</summary>
    BuiltIn,

    /// <summary>The template of the settings, for all connections (<see cref="AppSettings.AuditTemplatePath"/>).</summary>
    Global,

    /// <summary>The template of the connection's group (<see cref="AppSettings.AuditTemplateGroups"/>).</summary>
    Group,

    /// <summary>The connection's own template (<see cref="ConnectionProfile.AuditTemplatePath"/>).</summary>
    Connection,
}

/// <summary>The template in use, or why there is none.</summary>
/// <param name="Path">The file it comes from; null for the built-in template.</param>
/// <param name="Errors">Why the file cannot be used (not readable, template errors with their lines); empty if it can.</param>
public sealed record AuditTemplateLoad(AuditTemplate? Template, string? Path, IReadOnlyList<string> Errors)
{
    public bool IsDefault => Path is null;

    /// <summary>Where <see cref="Path"/> is set (<see cref="AuditTemplateSource.For"/>); <see cref="AuditTemplateOrigin.BuiltIn"/> without a path.</summary>
    public AuditTemplateOrigin Origin { get; init; }

    /// <summary>The group whose template it is (<see cref="AuditTemplateOrigin.Group"/>), as the settings name it.</summary>
    public string? Group { get; init; }
}

/// <summary>
/// The audit template of a connection (WP-26, WP-51): its own file, else its group's, else the one of the settings, else
/// the built-in one – each file read again when it changed on disk, so an edit in the company's repository counts at the
/// next look. A file that cannot be used is reported, not passed over: the next template down may be another system's.
/// </summary>
public sealed class AuditTemplateSource(AppSettingsService settings, TimeProvider? clock = null)
{
    /// <summary>
    /// How long a load is used without looking at the file again: views ask on every render (each keystroke in the
    /// designer), and a file on a share over VPN must not stall the UI thread each time (R5 review).
    /// </summary>
    public static readonly TimeSpan Fresh = CachedFileRead<AuditTemplateLoad>.Fresh;

    private readonly CachedFileRead<AuditTemplateLoad> _file = new(Read, Unreadable, clock);

    /// <summary>The template for tables of <paramref name="profile"/>; null for no connection (the global or built-in one).</summary>
    public AuditTemplateLoad For(ConnectionProfile? profile)
    {
        var current = settings.Current;
        if (profile?.AuditTemplatePath is { } own && !string.IsNullOrWhiteSpace(own))
        {
            return Load(own) with { Origin = AuditTemplateOrigin.Connection };
        }

        if (GroupEntry(current.AuditTemplateGroups, profile?.Group) is { } entry)
        {
            return Load(entry.Value) with { Origin = AuditTemplateOrigin.Group, Group = entry.Key };
        }

        var global = Load(current.AuditTemplatePath);
        return global.IsDefault ? global : global with { Origin = AuditTemplateOrigin.Global };
    }

    /// <summary>
    /// The entry of <paramref name="group"/> in <paramref name="groups"/>, matched as the connection lists match groups
    /// (trimmed, case ignored); null without one or for an entry without a path.
    /// </summary>
    public static KeyValuePair<string, string>? GroupEntry(IReadOnlyDictionary<string, string> groups, string? group)
    {
        ArgumentNullException.ThrowIfNull(groups);
        foreach (var entry in groups)
        {
            if (ConnectionSearch.SameGroup(entry.Key, group) && !string.IsNullOrWhiteSpace(entry.Value))
            {
                return entry;
            }
        }

        return null;
    }

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
