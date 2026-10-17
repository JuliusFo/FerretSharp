namespace FerretSharp.UI.State;

/// <summary>The version of this build (<c>Directory.Build.props</c>), as the status bar, the settings, the update dialog and the log show it (R5b: read four times).</summary>
public static class AppVersion
{
    /// <summary>"3.32.0"; "0.0.0" if the assembly has none.</summary>
    public static readonly string Current = typeof(AppVersion).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}
