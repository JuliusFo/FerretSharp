using FerretSharp.Core.Schema;

namespace FerretSharp.UI.State;

/// <summary>
/// One component's look at the <see cref="PresentationService"/>: did the C# model or the name setting change what it
/// shows since it last looked? A component renders only then – after a build most tables read as before, and every
/// mounted tab of every workspace would render (R5b, review 3.31.0 B12: the version bookkeeping stood in four components).
/// </summary>
public sealed class PresentationWatch(PresentationService presentations)
{
    private int _version = presentations.Version;

    /// <summary>Nothing changed at all since the last look.</summary>
    public bool Unchanged => presentations.Version == _version;

    /// <summary>The presentation of <paramref name="table"/> changed since the last look – without looking (<see cref="Seen"/>).</summary>
    public bool Touches(TableRef table) => presentations.ChangedSince(_version, table);

    /// <summary>The last look is now.</summary>
    public void Seen() => _version = presentations.Version;

    /// <summary>The presentation of <paramref name="table"/> changed since the last look; looks.</summary>
    public bool ChangedFor(TableRef table)
    {
        var changed = Touches(table);
        Seen();
        return changed;
    }

    /// <summary>The entity names shown beside tables (or the setting) changed since the last look; looks.</summary>
    public bool NamesChanged()
    {
        var changed = presentations.NamesChangedSince(_version);
        Seen();
        return changed;
    }
}
