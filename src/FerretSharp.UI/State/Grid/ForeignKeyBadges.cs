using FerretSharp.Core.Schema;

namespace FerretSharp.UI.State;

/// <summary>
/// The FK badges of a column header: a declared foreign key, a relationship of the C# model (R6b, review 3.40.0, B11: the
/// table grid and the result grid worked them out each).
/// </summary>
public static class ForeignKeyBadges
{
    /// <param name="outgoing">The table's outgoing foreign keys.</param>
    public static (bool Declared, bool Model) Of(IEnumerable<ForeignKeyInfo> outgoing, string column)
    {
        var over = outgoing.Where(f => f.FromColumns.Contains(column)).ToList();
        return (over.Any(f => f.Source == FkSource.Declared), over.Any(f => f.Source == FkSource.ClrModel));
    }
}
