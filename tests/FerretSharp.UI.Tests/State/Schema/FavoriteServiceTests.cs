using FerretSharp.Core.Schema;
using FerretSharp.UI.State;
using Microsoft.Extensions.Logging.Abstractions;

namespace FerretSharp.UI.Tests.State;

/// <summary>Favorites of the explorer (WP-91) over the real file: by the name shown for every connection, a broken file.</summary>
public sealed class FavoriteServiceTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly TableSummary KundenTest = new("ERP_TEST", "KUNDEN", TableKind.Table);
    private static readonly TableSummary KundenProd = new("ERP_PROD", "KUNDEN", TableKind.Table);
    private static readonly TableSummary AuftragTest = new("ERP_TEST", "AUFTRAG", TableKind.Table);
    private static readonly PlSqlObjectSummary Rechnung = new("ERP_TEST", "PKG_RECHNUNG", PlSqlKind.Package, "VALID");

    private readonly TestFolder _folder = new();

    private string StoreFile => _folder.Combine("favorites.json");

    public void Dispose() => _folder.Dispose();

    private async Task<FavoriteService> ServiceAsync()
    {
        var service = new FavoriteService(new FavoriteStore(StoreFile), NullLogger<FavoriteService>.Instance);
        await service.EnsureLoadedAsync();
        return service;
    }

    [Fact]
    public async Task A_favorite_holds_for_the_object_of_every_schema_is_kept_and_taken_back()
    {
        var service = await ServiceAsync();
        var changes = 0;
        service.Changed += () => changes++;

        Assert.Null(await service.ToggleAsync(Favorite.Of(KundenTest)));
        Assert.Null(await service.ToggleAsync(Favorite.Of(Rechnung)));

        Assert.Equal(2, changes);
        var reread = await ServiceAsync();
        Assert.True(reread.IsFavorite(KundenProd)); // by name: Prod sees the one marked on Test
        Assert.True(reread.IsFavorite(Rechnung));

        Assert.Null(await reread.ToggleAsync(Favorite.Of(KundenProd)));
        Assert.False((await ServiceAsync()).IsFavorite(KundenTest));
    }

    /// <summary>
    /// Review WP-91, 1: clicks faster than the file is written – a second heart while the first one saves, a double click on
    /// the same – each acts on what the one before left, none is lost.
    /// </summary>
    [Fact]
    public async Task Clicks_while_saving_act_one_after_the_other()
    {
        var service = await ServiceAsync();

        await Task.WhenAll(service.ToggleAsync(Favorite.Of(KundenTest)), service.ToggleAsync(Favorite.Of(Rechnung)),
            service.ToggleAsync(Favorite.Of(AuftragTest)), service.ToggleAsync(Favorite.Of(AuftragTest)));

        var reread = await ServiceAsync();
        Assert.True(reread.IsFavorite(KundenTest));
        Assert.True(reread.IsFavorite(Rechnung));
        Assert.False(reread.IsFavorite(AuftragTest)); // on and off again
    }

    /// <summary>A broken file keeps the favorites it holds: named as the reason, marking one is refused, the file stays.</summary>
    [Fact]
    public async Task A_broken_file_is_named_and_never_overwritten()
    {
        const string broken = """{ "version": 1, "favorites": 42 }""";
        await File.WriteAllTextAsync(StoreFile, broken, Ct);
        var service = await ServiceAsync();

        var error = await service.ToggleAsync(Favorite.Of(KundenTest));

        Assert.NotNull(service.LoadError);
        Assert.Equal(service.LoadError, error);
        Assert.False(service.IsFavorite(KundenTest));
        Assert.Equal(broken, await File.ReadAllTextAsync(StoreFile, Ct));
    }
}
