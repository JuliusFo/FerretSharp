using System.Collections.Concurrent;
using FerretSharp.Core.Connections;
using FerretSharp.Core.Links;

namespace FerretSharp.UI.State;

/// <summary>
/// Links from outside (WP-37, ADR 0026): the start argument and what a second start hands over to the running instance
/// (the host's pipe). Posted from any thread; the shell takes them on the UI thread and asks before opening one.
/// </summary>
public sealed class LinkInbox
{
    private readonly ConcurrentQueue<string> _links = new();

    /// <summary>A link was posted; may fire on a background thread.</summary>
    public event Action? Received;

    /// <summary>Takes a text that is meant as a link (<see cref="TableLink.IsLink"/>); anything else is dropped.</summary>
    public void Post(string? text)
    {
        if (!TableLink.IsLink(text))
        {
            return;
        }

        _links.Enqueue(text!);
        Received?.Invoke();
    }

    public bool TryTake(out string link) => _links.TryDequeue(out link!);
}

/// <summary>A link from outside, read and matched to a connection, for the confirmation (<c>LinkOpenDialog</c>).</summary>
/// <param name="Error">Why the link cannot be opened (invalid, from a newer version); null if it was read.</param>
public sealed record IncomingLink(TableLink? Link, string? Error, LinkConnectionMatch Match)
{
    public static IncomingLink Read(string text, IReadOnlyList<ConnectionProfile> profiles)
    {
        var read = TableLink.Parse(text);
        return read.Link is { } link
            ? new(link, null, LinkConnectionMatch.Find(profiles, link))
            : new(null, read.Error, new LinkConnectionMatch(null, []));
    }
}
