using FerretSharp.Core.Schema;
using FerretSharp.UI.State;
using Microsoft.Extensions.Time.Testing;

namespace FerretSharp.UI.Tests.State;

/// <summary>
/// WP-68: a new synonym goes into the browsed schema or PUBLIC; whether its name is taken is asked once typing rests, and
/// it cannot run before that answer – an older answer never counts for a newer name.
/// </summary>
public sealed class SynonymDraftTests
{
    private static readonly TableRef Target = new("OTHER", "KUNDE");

    private readonly FakeTimeProvider _time = new();
    private readonly List<(string Owner, string Name)> _asked = [];

    private SynonymDraft Draft(string? takenBy = null) => new(Target, "APP", (owner, name, _) =>
    {
        _asked.Add((owner, name));
        return Task.FromResult(takenBy);
    }, _time);

    private async Task TypeAsync(SynonymDraft draft, string name, bool isPublic = false)
    {
        var typing = draft.SetAsync(name, isPublic, CancellationToken.None);
        _time.Advance(SynonymDraft.CheckDelay);
        await typing;
    }

    [Fact]
    public async Task It_starts_with_the_object_s_name_and_runs_only_once_the_name_is_known_to_be_free()
    {
        var draft = Draft();
        Assert.Equal("KUNDE", draft.Name);

        var typing = draft.SetAsync("kunde_alt", isPublic: false, CancellationToken.None);
        Assert.Equal(["Prüfe, ob der Name frei ist …"], draft.Problems);
        _time.Advance(SynonymDraft.CheckDelay);
        await typing;

        Assert.Empty(draft.Problems);
        Assert.Equal([("APP", "KUNDE_ALT")], _asked);
        Assert.Equal("CREATE SYNONYM \"APP\".\"KUNDE_ALT\" FOR \"OTHER\".\"KUNDE\"", Assert.Single(draft.Steps).Sql);
        Assert.Empty(draft.Warnings);
    }

    [Fact]
    public async Task A_taken_name_says_by_what_and_a_public_synonym_warns_that_it_is_for_everyone()
    {
        var draft = Draft(takenBy: "TABLE");

        await TypeAsync(draft, "KUNDE");
        Assert.Equal(["KUNDE ist im Schema APP schon vergeben: Es gibt ein Objekt dieses Namens (TABLE)."], draft.Problems);

        await TypeAsync(draft, "KUNDE", isPublic: true);
        Assert.Equal(("PUBLIC", "KUNDE"), _asked[^1]);
        Assert.Equal(new SynonymInfo("PUBLIC", "KUNDE"), draft.Synonym);
        Assert.Equal(["Ein öffentliches Synonym gilt für alle Benutzer der Datenbank."], draft.Warnings);
    }

    [Fact]
    public async Task Typing_again_before_the_pause_asks_only_for_the_last_name()
    {
        var draft = Draft();

        var first = draft.SetAsync("K", isPublic: false, CancellationToken.None);
        var second = draft.SetAsync("KU", isPublic: false, CancellationToken.None);
        _time.Advance(SynonymDraft.CheckDelay);
        await Task.WhenAll(first, second);

        Assert.Equal([("APP", "KU")], _asked);
        Assert.Empty(draft.Problems);
    }

    /// <summary>Review of WP-68: the suggestion was the raw name – <c>Kunde Alt</c> became the synonym <c>"KUNDE ALT"</c>.</summary>
    [Fact]
    public void A_name_that_needs_quotes_is_suggested_quoted_and_warned_about()
    {
        var draft = new SynonymDraft(new TableRef("OTHER", "Kunde Alt"), "APP", (_, _, _) => Task.FromResult<string?>(null), _time);

        Assert.Equal("\"Kunde Alt\"", draft.Name);
        Assert.Equal(new SynonymInfo("APP", "Kunde Alt"), draft.Synonym);
        Assert.Equal(["Der Name ist kein einfacher Oracle-Name (Großbuchstaben, Ziffern, _ $ #, vorne ein Buchstabe); SQL und Code müssen das Synonym in doppelten Anführungszeichen schreiben."],
            draft.Warnings);
    }

    [Theory]
    [InlineData("", "Das Synonym braucht einen Namen.")]
    [InlineData("\"A\"B\"", "A\"B: Ein Name darf keine doppelten Anführungszeichen enthalten.")]
    public async Task A_name_Oracle_cannot_take_is_never_asked_for(string typed, string problem)
    {
        var draft = Draft();

        await TypeAsync(draft, typed);

        Assert.Equal([problem], draft.Problems);
        Assert.Empty(draft.Steps);
        Assert.Empty(_asked);
    }
}
