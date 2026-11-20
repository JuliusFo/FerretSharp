using FerretSharp.Core.Resources;
using FerretSharp.Core.Schema;
using FerretSharp.UI.Resources;
using Microsoft.Extensions.Logging;

namespace FerretSharp.UI.State;

/// <summary>
/// The favorites of the explorer (WP-91), for every connection: by kind and the name shown, without owner. Read once;
/// until a read succeeded nothing is written, so a file that was locked or broken at the start keeps the favorites it
/// holds (as the filter presets). Used from the UI thread only.
/// </summary>
public sealed class FavoriteService(FavoriteStore store, ILogger<FavoriteService> logger)
{
    private IReadOnlyList<Favorite> _favorites = [];
    private HashSet<Favorite> _set = [];
    private bool _loaded;
    private Task? _loading;

    /// <summary>Raised after the favorites were read or changed.</summary>
    public event Action? Changed;

    /// <summary>Why the favorites could not be read; null if fine. Marking one tries again.</summary>
    public string? LoadError { get; private set; }

    /// <summary>Counts reads and changes, for whoever keeps what it found among the favorites.</summary>
    public int Version { get; private set; }

    /// <summary>Reads the file unless it was read already; after an error it tries again. Callers at the same time share one read.</summary>
    public Task EnsureLoadedAsync()
    {
        if (!_loaded && _loading is not { IsCompleted: false })
        {
            _loading = LoadAsync();
        }

        return _loaded ? Task.CompletedTask : _loading!;
    }

    private async Task LoadAsync()
    {
        try
        {
            Use(await store.LoadAsync(CancellationToken.None));
            _loaded = true;
            LoadError = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Favorites could not be read from {File}", store.FilePath);
            LoadError = ex.Message;
            Version++;
        }

        Changed?.Invoke();
    }

    public bool IsFavorite(Favorite favorite) => _set.Contains(favorite);

    public bool IsFavorite(TableSummary table) => _set.Count > 0 && _set.Contains(Favorite.Of(table));

    public bool IsFavorite(PlSqlObjectSummary unit) => _set.Count > 0 && _set.Contains(Favorite.Of(unit));

    /// <summary>Marks <paramref name="favorite"/> as favorite or takes the mark away.</summary>
    /// <returns>The error to show, or null.</returns>
    public async Task<string?> ToggleAsync(Favorite favorite)
    {
        ArgumentNullException.ThrowIfNull(favorite);
        await EnsureLoadedAsync();
        if (!_loaded)
        {
            return LoadError ?? SchemaViewText.Favorites_NotLoaded;
        }

        if (!IsFavorite(favorite) && _favorites.Count >= FavoriteStore.MaxEntries)
        {
            return TextFormat.Format(SchemaViewText.Favorites_TooMany, FavoriteStore.MaxEntries);
        }

        var favorites = IsFavorite(favorite) ? _favorites.Where(f => f != favorite).ToList() : [.. _favorites, favorite];
        try
        {
            await store.SaveAsync(favorites, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Favorites could not be saved to {File}", store.FilePath);
            return TextFormat.Format(CommonText.SaveFailed, ex.Message);
        }

        Use(favorites);
        Changed?.Invoke();
        return null;
    }

    private void Use(IReadOnlyList<Favorite> favorites)
    {
        _favorites = favorites;
        _set = [.. favorites];
        Version++;
    }
}
