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
/// <param name="Notes">
/// The release notes per version, newest first: this version's and, where known, those of the versions between the
/// installed one and it (an update from 3.29.0 to 3.33.0 brings the changes of 3.30.0 to 3.33.0).
/// </param>
/// <param name="NotesComplete">
/// False if only this version's notes are known (<see cref="From"/> without the versions between): versions between the
/// installed one and this may be missing, the dialog points to all releases.
/// </param>
public sealed record AppUpdate(string Version, IReadOnlyList<VersionNotes> Notes, bool NotesComplete)
{
    /// <summary>
    /// The update to <paramref name="target"/> with the notes of <paramref name="between"/> – the versions after the
    /// installed one up to the target, in any order (the host knows them from the delta packages). Without them only the
    /// target's notes are known (<see cref="NotesComplete"/> false). The list is newest first, one entry per version, never
    /// newer than the target; blank notes are none.
    /// </summary>
    public static AppUpdate From(VersionNotes target, IEnumerable<VersionNotes>? between = null)
    {
        var versions = new List<VersionNotes> { Clean(target) };
        var complete = false;
        foreach (var notes in between ?? [])
        {
            complete = true;
            var index = versions.FindIndex(v => ReleaseVersion.Compare(v.Version, notes.Version) == 0);
            if (index < 0)
            {
                if (ReleaseVersion.Compare(notes.Version, target.Version) < 0)
                {
                    versions.Add(Clean(notes));
                }
            }
            else if (versions[index].Markdown is null)
            {
                versions[index] = Clean(notes) with { Version = versions[index].Version };
            }
        }

        versions.Sort((a, b) => ReleaseVersion.Compare(b.Version, a.Version));
        return new AppUpdate(target.Version, versions, complete);
    }

    private static VersionNotes Clean(VersionNotes notes) =>
        string.IsNullOrWhiteSpace(notes.Markdown) ? notes with { Markdown = null } : notes;
}

/// <param name="Version">The version as released, e.g. <c>3.29.0</c>.</param>
/// <param name="Markdown">Its release notes (Markdown, the version's section of the changelog); null if the release has none.</param>
public sealed record VersionNotes(string Version, string? Markdown);
