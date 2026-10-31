using FerretSharp.Core.Ddl;
using FerretSharp.Core.Oracle;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Schema;
using FerretSharp.UI.Resources;

namespace FerretSharp.UI.State;

/// <summary>
/// A new synonym in the "Synonyms" view (WP-68): name and kind as typed and whether the name is taken. A private one goes
/// into the browsed schema, never the login user's (the statement names the owner). Whether the name is taken is asked once
/// typing rests for <see cref="CheckDelay"/> – one small query, but over a VPN not one per key.
/// </summary>
/// <param name="target">The object the synonym points at (the real one, not a synonym of it).</param>
/// <param name="schemaOwner">The browsed schema, where a private synonym goes.</param>
/// <param name="taken">
/// The type of the object holding a name where a synonym of the owner would go (<c>PUBLIC</c> for a public one); null if it is
/// free or the check failed – Oracle then refuses with ORA-00955.
/// </param>
/// <param name="time">The clock of <see cref="CheckDelay"/> (tests); the system's if null.</param>
public sealed class SynonymDraft(TableRef target, string schemaOwner, Func<string, string, CancellationToken, Task<string?>> taken, TimeProvider? time = null)
{
    public static readonly TimeSpan CheckDelay = TimeSpan.FromMilliseconds(400);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private int _version;
    private SynonymInfo? _checked;
    private string? _takenBy;

    public TableRef Target { get; } = target;

    /// <summary>
    /// The name as typed; without quotes it is upper-cased (<see cref="OracleIdentifier.Normalize"/>). It starts as the
    /// object's name, quoted where Oracle needs it – <c>"Kunde Alt"</c> stays that, not <c>KUNDE ALT</c> (review of WP-68).
    /// </summary>
    public string Name { get; private set; } = OracleIdentifier.QuoteIfNeeded(target.Name);

    public bool Public { get; private set; }

    /// <summary>The synonym to create; null while the name is empty or cannot be created as written.</summary>
    public SynonymInfo? Synonym =>
        OracleIdentifier.Normalize(Name) is { Length: > 0 } name && OracleIdentifier.Problem(name) is null
            ? new SynonymInfo(Public ? SynonymInfo.PublicOwner : schemaOwner, name)
            : null;

    public IReadOnlyList<DdlStep> Steps => Synonym is { } synonym ? [SynonymDdl.Create(synonym, Target)] : [];

    /// <summary>What keeps it from running: no name, a name Oracle cannot take, the check still running, the name taken.</summary>
    public IReadOnlyList<string> Problems
    {
        get
        {
            var name = OracleIdentifier.Normalize(Name);
            if (name.Length == 0)
            {
                return [SchemaViewText.Synonyms_NameMissing];
            }

            if (OracleIdentifier.Problem(name) is { } problem)
            {
                return [problem];
            }

            return Synonym != _checked ? [SchemaViewText.Synonyms_Checking]
                : _takenBy is { } type ? [TextFormat.Format(Public ? SchemaViewText.Synonyms_TakenPublic : SchemaViewText.Synonyms_Taken, name, type, schemaOwner)]
                : [];
        }
    }

    /// <summary>
    /// A name SQL and code must write in double quotes (as the designers warn); a public synonym is there for every user of
    /// the database.
    /// </summary>
    public IReadOnlyList<string> Warnings =>
    [
        .. Synonym is { } synonym && OracleIdentifier.NeedsQuotes(synonym.Name) ? [SchemaViewText.Synonyms_NeedsQuotes] : Array.Empty<string>(),
        .. Public ? [SchemaViewText.Synonyms_PublicWarning] : Array.Empty<string>(),
    ];

    /// <summary>Takes what was typed and checks the name once typing rests; a newer change makes an older check give up.</summary>
    public async Task SetAsync(string name, bool isPublic, CancellationToken cancellationToken)
    {
        (Name, Public) = (name, isPublic);
        var version = ++_version;
        if (Synonym is not { } synonym || synonym == _checked)
        {
            return;
        }

        await Task.Delay(CheckDelay, _time, cancellationToken);
        if (version != _version)
        {
            return;
        }

        var type = await taken(synonym.Owner, synonym.Name, cancellationToken);
        if (version == _version)
        {
            (_checked, _takenBy) = (synonym, type);
        }
    }
}
