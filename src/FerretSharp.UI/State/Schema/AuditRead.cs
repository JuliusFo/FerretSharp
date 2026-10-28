using FerretSharp.Core.Audit;

namespace FerretSharp.UI.State;

/// <summary>
/// What the table designer read of its table's audit objects (WP-26): the state, whether a read is running, whether the
/// last one failed. Reads can overlap – "Reload" and the reload after a run that stopped halfway – and only the latest
/// counts: an earlier one finishing last left the state from before the run, and its end freed "Reload" while the
/// latest still read (code review 2026-10-28).
/// </summary>
public sealed class AuditRead
{
    private int _generation;
    private bool _failed;

    /// <summary>What the schema has; null while reading, after a failed read and when there was no template to read by.</summary>
    public AuditState? State { get; private set; }

    /// <summary>The latest read is running: the designer shows no script and "Reload" is locked.</summary>
    public bool Reading { get; private set; }

    /// <summary>
    /// Whether there is an audit table is not known though there could be: the read failed (also while reading again) or
    /// there was no template to read by. The designer says so and offers "Reload".
    /// </summary>
    public bool Unread => _generation > 0 && State is null && (_failed || !Reading);

    /// <summary>A read starts; the number is passed to <see cref="End"/>.</summary>
    public int Begin()
    {
        State = null;
        Reading = true;
        return ++_generation;
    }

    /// <summary>
    /// The read <paramref name="generation"/> ended; false when a later one started meanwhile – its result is dropped and
    /// the later one decides.
    /// </summary>
    public bool End(int generation, bool succeeded, AuditState? read)
    {
        if (generation != _generation)
        {
            return false;
        }

        Reading = false;
        _failed = !succeeded;
        State = succeeded ? read ?? AuditState.None : null;
        return true;
    }

    /// <summary>No template to read by: nothing is known until a read with one (a running read is dropped).</summary>
    public void WithoutTemplate()
    {
        _generation++;
        (State, Reading, _failed) = (null, false, false);
    }
}
