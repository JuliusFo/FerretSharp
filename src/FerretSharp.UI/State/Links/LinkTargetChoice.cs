using FerretSharp.Core.Connections;
using FerretSharp.Core.Links;

namespace FerretSharp.UI.State;

/// <summary>
/// The connection a link opens on (<c>LinkOpenDialog</c>, WP-37, ADR 0026 addendum): the one found, one of several that
/// fit, or one the user chooses from all connections – when none fits, or to open it elsewhere (another environment of
/// the same application, a colleague's connection under another name). Nothing is chosen by itself: without a match
/// "Open" stays disabled until the user picks one.
/// </summary>
public sealed class LinkTargetChoice
{
    private readonly IncomingLink _request;
    private readonly IReadOnlyList<ConnectionProfile> _profiles;
    private readonly IReadOnlyList<ConnectionProfile> _suggestions;
    private readonly IReadOnlyList<string>? _order;

    /// <param name="order">The user's order of the connection groups (WP-66), as the connection list shows them.</param>
    public LinkTargetChoice(IncomingLink request, IReadOnlyList<ConnectionProfile> profiles, IReadOnlyList<string>? order = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(profiles);
        _request = request;
        _profiles = profiles;
        _order = order;
        _suggestions = request.Link is { } link ? LinkConnectionMatch.Suggestions(profiles, link) : [];
        Choosing = request.Link is not null && request.Match.Profile is null && request.Match.Candidates.Count == 0 && profiles.Count > 0;
    }

    /// <summary>The list of all connections is shown: nothing fits, or the user asked for another connection.</summary>
    public bool Choosing { get; private set; }

    /// <summary>The search in the list of all connections.</summary>
    public string Query { get; set; } = "";

    /// <summary>The connection the user picked (a candidate or any from the list); null until then.</summary>
    public ConnectionProfile? Chosen { get; private set; }

    /// <summary>The connection the link opens on: the one picked, else the one found.</summary>
    public ConnectionProfile? Target => Chosen ?? _request.Match.Profile;

    /// <summary>The list offers something else than the candidates: there are connections the user may pick.</summary>
    public bool CanChooseOther => _request.Link is not null && _profiles.Count > _request.Match.Candidates.Count + (_request.Match.Profile is null ? 0 : 1);

    /// <summary>Connections that fit the link or read its schema (<see cref="LinkConnectionMatch.Suggestions"/>), searched.</summary>
    public IReadOnlyList<ConnectionProfile> Suggested => _suggestions.Where(p => ConnectionSearch.Matches(p, Query)).ToList();

    /// <summary>All other connections, grouped and searched like the connection list.</summary>
    public IReadOnlyList<ConnectionGroup> Others => ConnectionSearch.Group(_profiles.Except(_suggestions), Query, _order);

    /// <summary>
    /// The user opens the link on a connection it does not point at – not the one found, none of the candidates: the
    /// dialog names the link's own connection next to it.
    /// </summary>
    public bool Elsewhere => Chosen is { } chosen && chosen != _request.Match.Profile && !_request.Match.Candidates.Contains(chosen);

    /// <summary>The connection found by name only is the target (<see cref="LinkConnectionMatch.AddressDiffers"/>).</summary>
    public bool AddressDiffers => _request.Match.AddressDiffers && Target is { } target && target == _request.Match.Profile;

    /// <summary>Shows the list of all connections; the one found or picked stays picked.</summary>
    public void ChooseOther()
    {
        Choosing = true;
        Chosen ??= _request.Match.Profile;
    }

    public void Choose(ConnectionProfile profile) => Chosen = profile;
}
