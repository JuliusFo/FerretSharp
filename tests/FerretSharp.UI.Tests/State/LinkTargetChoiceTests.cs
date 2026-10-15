using FerretSharp.Core.Connections;
using FerretSharp.Core.Links;
using FerretSharp.Core.Workspaces;
using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>The connection a link opens on (ADR 0026 addendum): found, one of several, or one the user chooses from all.</summary>
public sealed class LinkTargetChoiceTests
{
    private static ConnectionProfile Profile(string name, string host, string user = "erp", string? group = null, ConnectionKind kind = ConnectionKind.Test) =>
        new(Guid.NewGuid(), name, kind, new HostPortAddress(host, 1521, "ORCLPDB", null), user, null, false, group);

    private static readonly ConnectionProfile ErpTest = Profile("ERP Test", "db01", group: "ERP");
    private static readonly ConnectionProfile ErpProd = Profile("ERP Prod", "db02", group: "ERP", kind: ConnectionKind.Prod);
    private static readonly ConnectionProfile Kasse = Profile("Kasse", "db03", user: "kasse", group: "Kasse");

    /// <summary>A link to ERP.AUFTRAG on a colleague's connection.</summary>
    private static IncomingLink Request(string connection, string host, IReadOnlyList<ConnectionProfile> profiles) =>
        IncomingLink.Read(TableLink.For(Profile(connection, host), new("ERP", "AUFTRAG"), [], [], TabMode.Data).ToUri(), profiles);

    [Fact]
    public void Without_a_match_the_list_shows_and_nothing_is_picked_until_the_user_does()
    {
        ConnectionProfile[] profiles = [Kasse, ErpProd, ErpTest];
        var choice = new LinkTargetChoice(Request("Kollegen-ERP", "db99", profiles), profiles);

        Assert.True(choice.Choosing);
        Assert.Null(choice.Target);
        Assert.Equal([ErpTest, ErpProd], choice.Suggested); // the link's schema ERP, in list order
        Assert.Equal(["Kasse"], choice.Others.Select(g => g.Name));

        choice.Choose(ErpTest);

        Assert.Same(ErpTest, choice.Target);
        Assert.True(choice.Elsewhere);
        Assert.False(choice.AddressDiffers);
    }

    [Fact]
    public void A_found_connection_can_be_changed_and_then_the_link_opens_elsewhere()
    {
        ConnectionProfile[] profiles = [Kasse, ErpProd, ErpTest];
        var choice = new LinkTargetChoice(Request("ERP Test", "db01", profiles), profiles);

        Assert.False(choice.Choosing);
        Assert.Same(ErpTest, choice.Target);
        Assert.True(choice.CanChooseOther);

        choice.ChooseOther();
        Assert.True(choice.Choosing);
        Assert.Same(ErpTest, choice.Target); // stays picked
        Assert.False(choice.Elsewhere);

        choice.Choose(ErpProd);
        Assert.Same(ErpProd, choice.Target);
        Assert.True(choice.Elsewhere);
    }

    [Fact]
    public void A_candidate_is_no_other_connection_and_a_name_only_match_warns_only_while_it_is_the_target()
    {
        var a = Profile("A", "db01");
        var b = Profile("B", "db01");
        ConnectionProfile[] both = [a, b, Kasse];
        var candidates = new LinkTargetChoice(Request("X", "db01", both), both);

        Assert.False(candidates.Choosing);
        Assert.Null(candidates.Target);
        candidates.Choose(b);
        Assert.False(candidates.Elsewhere);

        var sameName = Profile("ERP Test", "db77");
        ConnectionProfile[] named = [sameName, ErpProd];
        var byName = new LinkTargetChoice(Request("ERP Test", "db01", named), named);
        Assert.True(byName.AddressDiffers);
        byName.ChooseOther();
        byName.Choose(ErpProd);
        Assert.False(byName.AddressDiffers);
    }

    [Fact]
    public void The_search_narrows_suggestions_and_groups()
    {
        ConnectionProfile[] profiles = [Kasse, ErpProd, ErpTest];
        var choice = new LinkTargetChoice(Request("Kollegen-ERP", "db99", profiles), profiles) { Query = "prod" };

        Assert.Equal([ErpProd], choice.Suggested);
        Assert.Empty(choice.Others);
    }

    [Fact]
    public void An_invalid_link_or_no_connections_offer_nothing_to_choose()
    {
        ConnectionProfile[] profiles = [ErpTest];
        var invalid = new LinkTargetChoice(IncomingLink.Read("ferretsharp://open?v=9&s=ERP&t=AUFTRAG", profiles), profiles);
        var none = new LinkTargetChoice(Request("ERP Test", "db01", []), []);

        Assert.False(invalid.Choosing);
        Assert.False(invalid.CanChooseOther);
        Assert.False(none.Choosing);
        Assert.Null(none.Target);
    }
}
