namespace FerretSharp.UI.State;

/// <summary>
/// Updates of an installed FerretSharp from the GitHub releases (WP-32, ADR 0024). Implemented by the host with Velopack;
/// the UI only reports and acts on a click (<see cref="UpdateState"/>).
/// </summary>
public interface IAppUpdater
{
    /// <summary>
    /// False for a FerretSharp that was not installed (a build, <c>dotnet run</c>, the plain zip): there is nothing to check
    /// and nothing to apply.
    /// </summary>
    bool IsInstalled { get; }

    /// <summary>A version downloaded before and not installed yet (the app ended before the restart); null if none.</summary>
    AppUpdate? Downloaded { get; }

    /// <summary>The newest version if it is newer than this one; null if this is the newest.</summary>
    /// <exception cref="Exception">The releases could not be read (offline, VPN, rate limit) – any exception, the UI shows its message.</exception>
    Task<AppUpdate?> CheckAsync(CancellationToken cancellationToken);

    /// <summary>Downloads the update (delta packages where possible); <paramref name="progress"/> gets 0–100.</summary>
    Task DownloadAsync(AppUpdate update, Action<int> progress, CancellationToken cancellationToken);

    /// <summary>
    /// Starts the updater, which waits for this process to end, installs the downloaded update and starts the new version.
    /// The caller ends the app right after (<see cref="ExitGuard.RequestClose"/>).
    /// </summary>
    void ApplyAfterExit(AppUpdate update);
}

/// <param name="Version">The version as released, e.g. <c>3.29.0</c>.</param>
/// <param name="Notes">The release notes (Markdown, the version's section of the changelog); null if the release has none.</param>
public sealed record AppUpdate(string Version, string? Notes);
