using FerretSharp.Core.Resources;
using FerretSharp.Core.Schema;
using FerretSharp.UI.Resources;

namespace FerretSharp.UI.State;

/// <summary>What the head of a column menu says about the column (WP-61), from the schema and the relationships.</summary>
public static class ColumnFacts
{
    /// <summary>
    /// The foreign keys over <paramref name="column"/> among the table's outgoing ones (constraints, the C# model, manual
    /// ones), each target once – the constraint first, as the schema lists it.
    /// </summary>
    public static IReadOnlyList<ForeignKeyInfo> ForeignKeysOf(IEnumerable<ForeignKeyInfo> outgoing, string column) =>
        [.. outgoing.Where(fk => fk.FromColumns.Contains(column)).DistinctBy(fk => fk.To)];

    /// <summary>
    /// "FK_POS_AUFTRAG: APP.POSITION(AUFTRAG_ID) → APP.AUFTRAG(ID)", and that it is a relationship of the C# model without a
    /// constraint.
    /// </summary>
    public static string Title(ForeignKeyInfo fk) =>
        $"{fk.Name}: {fk.From}({string.Join(", ", fk.FromColumns)}) → {fk.To}({string.Join(", ", fk.ToColumns)})" +
        (fk.Source == FkSource.ClrModel ? Environment.NewLine + GridText.FkJump_ModelRelationship : "");

    /// <summary>"Copy name" of both column menus (review 3.54.0, B5): the name, with a notice when it is in the clipboard.</summary>
    public static async Task CopyNameAsync(ShellState shell, DomModule dom, string name)
    {
        if (await dom.CopyTextAsync(shell, name))
        {
            shell.Notify(TextFormat.Format(GridText.ColumnMenu_NameCopied, name));
        }
    }
}
