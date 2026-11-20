using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Tests.Schema;

/// <summary>Favorites of the explorer (WP-91): by kind and the name shown, without owner; one file for all connections.</summary>
public sealed class FavoritesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void A_favorite_is_the_name_shown_without_owner_and_the_kind()
    {
        var synonym = new TableSummary("STAMM", "KUNDE_T", TableKind.Table, new SynonymInfo("APP", "KUNDEN"));

        Assert.Equal(new Favorite(SchemaObjectKind.Table, "KUNDEN"), Favorite.Of(synonym));
        Assert.Equal(Favorite.Of(new TableSummary("DEV_APP", "AUFTRAG", TableKind.Table)), Favorite.Of(new TableSummary("PROD_APP", "AUFTRAG", TableKind.View)));
        Assert.Equal(new Favorite(SchemaObjectKind.Trigger, "TRG_AUFTRAG"), Favorite.Of(new PlSqlObjectSummary("APP", "TRG_AUFTRAG", PlSqlKind.Trigger, "VALID")));
        Assert.NotEqual(Favorite.Of(new PlSqlObjectSummary("APP", "AUFTRAG", PlSqlKind.Trigger, "VALID")), Favorite.Of(new TableSummary("APP", "AUFTRAG", TableKind.Table)));
    }

    [Fact]
    public void Kinds_go_both_ways()
    {
        foreach (var kind in Enum.GetValues<PlSqlKind>())
        {
            Assert.Equal(kind, SchemaObjects.PlSqlKindOf(SchemaObjects.KindOf(kind)));
        }

        Assert.Null(SchemaObjects.PlSqlKindOf(SchemaObjectKind.Table));
    }

    [Fact]
    public async Task Favorites_survive_a_round_trip()
    {
        using var folder = new TestFolder();
        var store = new FavoriteStore(folder.Combine("data", "favorites.json")); // the folder does not exist yet
        Assert.Empty(await store.LoadAsync(Ct));
        Favorite[] favorites = [new(SchemaObjectKind.Table, "KUNDEN"), new(SchemaObjectKind.Package, "PKG_RECHNUNG")];

        await store.SaveAsync(favorites, Ct);

        Assert.Equal(favorites, await store.LoadAsync(Ct));
        Assert.Contains("\"kind\": \"package\"", await File.ReadAllTextAsync(store.FilePath, Ct)); // enums as text
    }

    /// <summary>Hand edits: null entries, blank names, unknown kinds and doubles are left out; a null list is empty.</summary>
    [Fact]
    public async Task Files_edited_by_hand_are_cleaned_up()
    {
        using var folder = new TestFolder();
        var file = folder.Combine("favorites.json");
        await File.WriteAllTextAsync(file, """
            { "version": 1, "favorites": [
              null,
              { "kind": "table", "name": "KUNDEN" },
              { "kind": "table", "name": "  " },
              { "kind": "table" },
              { "kind": 42, "name": "SELTSAM" },
              { "kind": "table", "name": "KUNDEN" },
              { "kind": "trigger", "name": "KUNDEN" }
            ] }
            """, Ct);

        Assert.Equal([new(SchemaObjectKind.Table, "KUNDEN"), new Favorite(SchemaObjectKind.Trigger, "KUNDEN")], await new FavoriteStore(file).LoadAsync(Ct));

        await File.WriteAllTextAsync(file, """{ "version": 1, "favorites": null }""", Ct);
        Assert.Empty(await new FavoriteStore(file).LoadAsync(Ct));
    }

    [Theory]
    [InlineData("""{ "version": 1, "favorites": 42 }""", "keine gültige Datei mit Favoriten", "nicht überschrieben")]
    [InlineData("""{ "version": 2, "favorites": [], "somethingNew": true }""", "neueren FerretSharp-Version", "nicht überschrieben")]
    public async Task Broken_or_newer_files_are_reported_and_never_overwritten(string content, string loadError, string saveError)
    {
        using var folder = new TestFolder();
        var file = folder.Combine("favorites.json");
        await File.WriteAllTextAsync(file, content, Ct);
        var store = new FavoriteStore(file);

        var load = await Assert.ThrowsAsync<IOException>(() => store.LoadAsync(Ct));
        var save = await Assert.ThrowsAsync<IOException>(() => store.SaveAsync([new(SchemaObjectKind.Table, "NEU")], Ct));

        Assert.Contains(loadError, load.Message);
        Assert.Contains(saveError, save.Message);
        Assert.Equal(content, await File.ReadAllTextAsync(file, Ct));
    }
}
