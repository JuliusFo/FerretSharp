using FerretSharp.Core.Resources;
using FerretSharp.Core.Settings;
using FerretSharp.UI.Resources;

namespace FerretSharp.UI.State;

/// <summary>The sections of the settings page (WP-34), in the order of the list at the left.</summary>
public enum SettingsPart { Appearance, ClrModel, Connection, Audit, DisplayColumns, Updates, Diagnostics, Shortcuts }

/// <summary>The rows of the settings page outside the shortcuts; the page asks the filter by these keys.</summary>
public enum SettingKey
{
    Language,
    ColorScheme,
    ClrNames,
    KeepAlive,
    LockWait,
    AuditTemplate,
    ShowDisplayNames,
    DisplayColumnsFile,
    Version,
    CheckForUpdates,
    DiagnoseUiStalls,
}

/// <summary>One row of the settings page as the search sees it.</summary>
/// <param name="Id">The row's identity (<see cref="SettingsCatalog.IdOf(SettingKey)"/>, <see cref="SettingsCatalog.IdOf(ShortcutAction)"/>, <see cref="SettingsCatalog.IdOf(FixedShortcut)"/>).</param>
/// <param name="Hint">The hint as the page shows it.</param>
/// <param name="Group">The group of a shortcut; null for a setting.</param>
/// <param name="Words">What the page shows besides label and hint: code in the hint, the key of a shortcut.</param>
public sealed record SettingsEntry(string Id, SettingsPart Part, string Label, string? Hint, string? Group, IReadOnlyList<string> Words);

/// <summary>
/// Everything the settings page shows, for its search (WP-34): one list, so a row the page shows cannot be missing from the
/// search (<c>SettingsCatalogTests</c> checks every <see cref="SettingKey"/>), and the code shown in hints is a constant here
/// that the page uses as well.
/// </summary>
public static class SettingsCatalog
{
    public const string KeepAliveQuery = "SELECT 1 FROM DUAL";
    public const string KeepAliveProfileLimit = "IDLE_TIME";
    public const string LockWaitClause = "SELECT … FOR UPDATE WAIT n";
    public const string DisplayNameExample = "4711 · PetersPizza";
    public const string StallTool = "dotnet-stack";
    public const string StallFiles = "stall-*.txt";

    // Properties, not static fields: the texts would keep the language of the first access.
    public static IReadOnlyList<(SettingsPart Part, string Label)> Parts =>
    [
        (SettingsPart.Appearance, ShellText.Settings_Appearance),
        (SettingsPart.ClrModel, ShellText.Settings_ClrModel),
        (SettingsPart.Connection, ShellText.Settings_Connection),
        (SettingsPart.Audit, ShellText.Settings_Audit),
        (SettingsPart.DisplayColumns, ShellText.Settings_DisplayColumns),
        (SettingsPart.Updates, ShellText.Settings_Updates),
        (SettingsPart.Diagnostics, ShellText.Settings_Diagnostics),
        (SettingsPart.Shortcuts, ShellText.Settings_Shortcuts),
    ];

    public static string LabelOf(SettingsPart part) => Parts.First(p => p.Part == part).Label;

    public static string IdOf(SettingKey key) => "setting:" + key;

    public static string IdOf(ShortcutAction action) => "shortcut:" + action;

    public static string IdOf(FixedShortcut fixedKey) => "fixed:" + fixedKey.Group + "/" + fixedKey.Label;

    /// <summary>The rows of the page as it shows them now: "Check at start" only for an installed FerretSharp.</summary>
    public static IReadOnlyList<SettingsEntry> Entries(ShortcutMap shortcuts, bool installed)
    {
        ArgumentNullException.ThrowIfNull(shortcuts);
        List<SettingsEntry> entries =
        [
            Setting(SettingKey.Language, SettingsPart.Appearance, ShellText.Settings_Language, ShellText.Settings_LanguageHint),
            Setting(SettingKey.ColorScheme, SettingsPart.Appearance, ShellText.Settings_ColorScheme, ShellText.Settings_ColorSchemeHint),
            Setting(SettingKey.ClrNames, SettingsPart.ClrModel, ShellText.Settings_ClrNames, ShellText.Settings_ClrNamesHint),
            Setting(SettingKey.KeepAlive, SettingsPart.Connection, ShellText.Settings_KeepAlive,
                TextFormat.Format(ShellText.Settings_KeepAliveHint, KeepAliveQuery, KeepAliveProfileLimit)),
            Setting(SettingKey.LockWait, SettingsPart.Connection, ShellText.Settings_LockWait, TextFormat.Format(ShellText.Settings_LockWaitHint, LockWaitClause)),
            Setting(SettingKey.AuditTemplate, SettingsPart.Audit, ShellText.Settings_AuditTemplate, ShellText.Settings_AuditTemplateHint),
            Setting(SettingKey.ShowDisplayNames, SettingsPart.DisplayColumns, ShellText.Settings_ShowDisplayNames,
                TextFormat.Format(ShellText.Settings_ShowDisplayNamesHint, DisplayNameExample)),
            Setting(SettingKey.DisplayColumnsFile, SettingsPart.DisplayColumns, ShellText.Settings_DisplayColumnsFile, ShellText.Settings_DisplayColumnsFileHint),
            Setting(SettingKey.Version, SettingsPart.Updates, ShellText.Settings_Version,
                installed ? ShellText.Settings_VersionInstalled : ShellText.Settings_VersionNotInstalled),
            Setting(SettingKey.DiagnoseUiStalls, SettingsPart.Diagnostics, ShellText.Settings_DiagnoseUiStalls,
                TextFormat.Format(ShellText.Settings_DiagnoseUiStallsHint, StallTool, StallFiles)),
        ];
        if (installed)
        {
            entries.Insert(entries.FindIndex(e => e.Id == IdOf(SettingKey.Version)) + 1,
                Setting(SettingKey.CheckForUpdates, SettingsPart.Updates, ShellText.Settings_CheckForUpdates, ShellText.Settings_CheckForUpdatesHint));
        }

        entries.AddRange(ShortcutMap.Definitions.Select(d => new SettingsEntry(
            IdOf(d.Action), SettingsPart.Shortcuts, d.Label, d.Hint, d.Group, shortcuts.Chord(d.Action) is { } chord ? [chord.Label] : [])));
        entries.AddRange(ShortcutMap.Fixed.Select(f => new SettingsEntry(IdOf(f), SettingsPart.Shortcuts, f.Label, f.Hint, f.Group, [f.Keys])));
        return entries;
    }

    private static SettingsEntry Setting(SettingKey key, SettingsPart part, string label, string hint) => new(IdOf(key), part, label, hint, null, []);
}

/// <summary>
/// What the settings page shows for a search (WP-34), worked out once per query: without a query the selected section with
/// all its rows; with one every section with a matching row, only those rows, and the shortcut groups that have one.
/// </summary>
public sealed class SettingsFilter
{
    private readonly HashSet<string> _matching = [];
    private readonly HashSet<SettingsPart> _parts = [];
    private readonly HashSet<string> _groups = [];

    public SettingsFilter(SettingsSearch search, IReadOnlyList<SettingsEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(search);
        ArgumentNullException.ThrowIfNull(entries);
        Search = search;
        foreach (var entry in entries.Where(e => search.Matches([e.Label, e.Hint, e.Group, SettingsCatalog.LabelOf(e.Part), .. e.Words])))
        {
            _matching.Add(entry.Id);
            _parts.Add(entry.Part);
            if (entry.Group is { } group)
            {
                _groups.Add(group);
            }
        }
    }

    public SettingsSearch Search { get; }

    public bool IsActive => Search.IsActive;

    /// <summary>A search that matches nothing at all.</summary>
    public bool IsEmpty => IsActive && _parts.Count == 0;

    /// <summary>Whether the section shows: the selected one without a search, else one with a matching row.</summary>
    public bool ShowsSection(SettingsPart part, SettingsPart selected) => IsActive ? _parts.Contains(part) : part == selected;

    /// <summary>A row (<see cref="SettingsCatalog.IdOf(SettingKey)"/> …) shows: always without a search, else if it matches.</summary>
    public bool Shows(string id) => !IsActive || _matching.Contains(id);

    public bool Shows(SettingKey key) => Shows(SettingsCatalog.IdOf(key));

    /// <summary>A group of shortcuts shows: always without a search, else if one of its shortcuts matches.</summary>
    public bool ShowsGroup(string group) => !IsActive || _groups.Contains(group);
}
