using FerretSharp.Core.Schema;

namespace FerretSharp.UI.State;

/// <summary>
/// Which tables new foreign keys reference the designer loads (<see cref="TableDesignerDraft.UnknownReferences"/>): each
/// once. A failed load is not retried by itself – the designer asks after every render, a connection that stays gone would
/// try again and again –, but choosing the table again, or a load that could not start (no schema), asks anew (review of
/// the model constraints: before, a failed load left a foreign key without referenced columns until the designer closed).
/// </summary>
public sealed class ReferencedTableLoads
{
    private readonly HashSet<TableRef> _asked = [];

    /// <summary>The tables of <paramref name="unknown"/> not asked for yet; from now on they are.</summary>
    public IReadOnlyList<TableRef> Due(IEnumerable<TableRef> unknown) => unknown.Where(_asked.Add).ToList();

    /// <summary>The table was chosen again, or its load could not start: the next <see cref="Due"/> names it again.</summary>
    public void Again(TableRef table) => _asked.Remove(table);
}
