using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>The filter field of the right-click menus (WP-61): the rule of every search box, names as the palette finds them.</summary>
public sealed class MenuFilterTests
{
    [Fact]
    public void The_menu_filter_keeps_entries_with_every_typed_word_in_one_of_their_labels()
    {
        var filter = new MenuFilter();
        Assert.False(filter.Active);
        Assert.True(filter.Matches("Copy as table"));

        filter.Set("  exc  ROH ");

        Assert.True(filter.Active);
        Assert.True(filter.Matches("Excel (Rohwerte)", null));
        Assert.True(filter.Matches("Excel", "Rohwerte speichern"));
        Assert.False(filter.Matches("Excel"));
        Assert.False(filter.Matches(null, "CSV"));
    }

    /// <summary>Review 3.54.0, A8: the menus had a rule of their own – accents counted, underscores could not be left out.</summary>
    [Fact]
    public void Accents_are_ignored_and_names_are_found_without_their_underscores()
    {
        var filter = new MenuFilter();

        filter.Set("anderung");
        Assert.True(filter.Matches("Ausstehende Änderungen verwerfen"));

        filter.Set("kundeadresse");
        Assert.False(filter.Matches("KUNDE_ADRESSE"));
        Assert.True(filter.MatchesName("KUNDE_ADRESSE"));
        Assert.False(filter.MatchesName("KUNDE_ART"));
    }

    /// <summary>Review 3.54.0, A22: a heading stays exactly while one of its lines does – each line with the heading's words.</summary>
    [Fact]
    public void A_heading_stays_while_one_of_its_lines_does()
    {
        var filter = new MenuFilter();
        filter.Set("bearbeiten löschen");

        Assert.True(filter.KeepsHeading("Bearbeiten · 3 Zeilen", ["Zeile löschen", null]));
        Assert.False(filter.KeepsHeading("Bearbeiten · 3 Zeilen", ["Wert setzen", null]));
        Assert.False(filter.KeepsHeading("Kopieren", ["Zeile löschen"]));
    }
}
