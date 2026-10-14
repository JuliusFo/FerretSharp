using System.IO;
using FerretSharp.Core.Settings;
using FerretSharp.UI.State;
using Velopack;
using Velopack.Sources;

namespace FerretSharp.App.Services;

/// <summary>
/// Updates with Velopack (WP-32, ADR 0024) from the releases of the public GitHub repository, without a token (60 requests
/// per hour and address – one check per start). <c>--update-source=&lt;folder&gt;</c> reads a local folder instead, for
/// testing an update without GitHub.
/// </summary>
internal sealed class VelopackUpdater : IAppUpdater
{
    public const string RepositoryUrl = "https://github.com/JuliusFo/FerretSharp";

    private readonly UpdateManager _manager;
    private readonly string[] _restartArguments;
    private UpdateInfo? _found;

    public VelopackUpdater(string[] args)
    {
        var folder = CommandLine.ValueOf(args, CommandLine.UpdateSource);
        IUpdateSource source = string.IsNullOrWhiteSpace(folder)
            ? new GithubSource(RepositoryUrl, accessToken: null, prerelease: false)
            : new SimpleFileSource(new DirectoryInfo(Path.GetFullPath(folder)));
        _manager = new UpdateManager(source);
        // The new version starts as this one did (data folder, language) – with full paths: Velopack's updater may start it
        // in another working folder, and a relative --data-dir would then point at an empty one (review 3.31.0, A44).
        _restartArguments = [.. CommandLine.Restart(args)];
    }

    public bool IsInstalled => _manager.IsInstalled;

    /// <summary>Only the target's notes: what lay between is not known any more once the download waits for the restart.</summary>
    public AppUpdate? Downloaded => IsInstalled && _manager.UpdatePendingRestart is { } pending ? AppUpdate.From(Notes(pending)) : null;

    public async Task<AppUpdate?> CheckAsync(CancellationToken cancellationToken)
    {
        var found = await _manager.CheckForUpdatesAsync();
        _found = found;
        // The delta packages after the installed version up to the target each carry their version's notes (an update from
        // 3.29.0 to 3.33.0 has those of 3.30.0 to 3.33.0). None without a full package of the installed version (portable)
        // or without a delta for the target: then only the target's notes.
        return found is null ? null : AppUpdate.From(Notes(found.TargetFullRelease), found.DeltasToTarget.Select(Notes));
    }

    public Task DownloadAsync(AppUpdate update, Action<int> progress, CancellationToken cancellationToken)
    {
        if (_found is not { } found || found.TargetFullRelease.Version.ToString() != update.Version)
        {
            throw new InvalidOperationException($"Update {update.Version} was not found by the last check.");
        }

        return _manager.DownloadUpdatesAsync(found, progress, cancellationToken);
    }

    public void ApplyAfterExit(AppUpdate update)
    {
        var asset = _manager.UpdatePendingRestart ?? throw new InvalidOperationException($"Update {update.Version} is not downloaded.");
        _manager.WaitExitThenApplyUpdates(asset, silent: false, restart: true, _restartArguments);
    }

    private static VersionNotes Notes(VelopackAsset asset) => new(asset.Version.ToString(), asset.NotesMarkdown);
}
