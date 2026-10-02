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
}
