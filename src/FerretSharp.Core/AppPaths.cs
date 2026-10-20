namespace FerretSharp.Core;

/// <summary>
/// Well-known locations for per-user application data (connections, workspaces, logs).
/// </summary>
public sealed class AppPaths
{
    public const string AppFolderName = "FerretSharp";

    public AppPaths(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = root;
    }

    /// <summary>%APPDATA%\FerretSharp on Windows, ~/.config/FerretSharp on Linux.</summary>
    public static AppPaths Default { get; } = new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppFolderName));

    public string Root { get; }

    public string LogsDirectory => Path.Combine(Root, "logs");

    public string ConnectionsFile => Path.Combine(Root, "connections.json");

    public string RecentConnectionsFile => Path.Combine(Root, "recent.json");

    public string WorkspacesDirectory => Path.Combine(Root, "workspaces");

    public string SettingsFile => Path.Combine(Root, "settings.json");

    /// <summary>Exported C# models of linked projects, reused while their build is unchanged (WP-16).</summary>
    public string ModelCacheDirectory => Path.Combine(Root, "cache", "models");

    /// <summary>What the IL scanner read from each assembly of a linked solution, one file per MVID (WP-40).</summary>
    public string CodeUsageCacheDirectory => Path.Combine(Root, "cache", "code-usage");

    /// <summary>The active work clone of each connection group and the recently chosen folders (WP-40).</summary>
    public string WorkClonesFile => Path.Combine(Root, "work-clones.json");

    /// <summary>The SQL editor's history, one file per connection (WP-17).</summary>
    public string SqlHistoryDirectory => Path.Combine(Root, "history");

    /// <summary>Saved schema comparisons (WP-20).</summary>
    public string ComparisonsFile => Path.Combine(Root, "comparisons.json");

    /// <summary>Filter presets of all tables (WP-37).</summary>
    public string FilterPresetsFile => Path.Combine(Root, "filter-presets.json");

    /// <summary>Display columns (WP-31) unless the settings name another file.</summary>
    public string DisplayColumnsFile => Path.Combine(Root, "display-columns.json");

    /// <summary>Held by the running instance for this data folder (WP-32, <see cref="IO.InstanceLock"/>).</summary>
    public string InstanceLockFile => Path.Combine(Root, "instance.lock");

    /// <summary>The process id of the instance holding <see cref="InstanceLockFile"/>, so a second start can bring its window forward.</summary>
    public string InstanceProcessFile => Path.Combine(Root, "instance.pid");
}
