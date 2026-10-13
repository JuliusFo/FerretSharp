using System.IO;
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

    private const string SourceArgument = "--update-source=";

    private readonly UpdateManager _manager;
    private readonly string[] _restartArguments;
    private UpdateInfo? _found;

    public VelopackUpdater(string[] args)
    {
        var folder = args.FirstOrDefault(a => a.StartsWith(SourceArgument, StringComparison.OrdinalIgnoreCase))?[SourceArgument.Length..];
        IUpdateSource source = string.IsNullOrWhiteSpace(folder)
            ? new GithubSource(RepositoryUrl, accessToken: null, prerelease: false)
            : new SimpleFileSource(new DirectoryInfo(Path.GetFullPath(folder)));
        _manager = new UpdateManager(source);
        // The new version starts as this one did (data folder, language) – with full paths: Velopack's updater may start it
        // in another working folder, and a relative --data-dir would then point at an empty one (review 3.31.0, A44).
        _restartArguments = args.Select(WithFullPath).ToArray();
    }

    public bool IsInstalled => _manager.IsInstalled;

    public AppUpdate? Downloaded => IsInstalled && _manager.UpdatePendingRestart is { } pending ? ToUpdate(pending) : null;

    public async Task<AppUpdate?> CheckAsync(CancellationToken cancellationToken)
    {
        var found = await _manager.CheckForUpdatesAsync();
        _found = found;
        return found is null ? null : ToUpdate(found.TargetFullRelease);
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

    private static readonly string[] PathArguments = ["--data-dir=", "--import-appdata=", SourceArgument];

    private static string WithFullPath(string argument) =>
        PathArguments.FirstOrDefault(p => argument.StartsWith(p, StringComparison.OrdinalIgnoreCase)) is { } prefix
        && argument.Length > prefix.Length
            ? prefix + Path.GetFullPath(argument[prefix.Length..])
            : argument;

    private static AppUpdate ToUpdate(VelopackAsset asset) =>
        new(asset.Version.ToString(), string.IsNullOrWhiteSpace(asset.NotesMarkdown) ? null : asset.NotesMarkdown);
}
