using System.Globalization;
using FerretSharp.Core.Audit;
using FerretSharp.Core.Design;
using FerretSharp.Core.Oracle;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Schema;
using FerretSharp.UI.Resources;

namespace FerretSharp.UI.State;

/// <summary>
/// An existing constraint in the edit mode, as its fields show it: its name, a check's condition, a foreign key's delete
/// rule – renamed or changed, it becomes a <see cref="ConstraintEdit"/>.
/// </summary>
public sealed class ExistingConstraint
{
    public ExistingConstraint(ConstraintInfo before)
    {
        ArgumentNullException.ThrowIfNull(before);
        Before = before;
        Name = before.Name;
        Condition = before.Condition?.Trim() ?? "";
        DeleteRule = before.OnDelete ?? "";
    }

    /// <summary>The constraint in the table.</summary>
    public ConstraintInfo Before { get; }

    public string Name { get; set; }

    /// <summary>A check's condition, as typed.</summary>
    public string Condition { get; set; }

    /// <summary>A foreign key's <c>CASCADE</c>, <c>SET NULL</c> or empty for none.</summary>
    public string DeleteRule { get; set; }

    /// <summary>A renamed key's index of the same name gets the new name too.</summary>
    public bool RenameIndex { get; set; } = true;

    public bool IsRenamed => OracleIdentifier.Normalize(Name) != Before.Name;

    /// <summary>Renamed, a check with another condition, a foreign key with another delete rule.</summary>
    public bool IsChanged => IsRenamed || TableDesign.Rechecks(Before, ToEdit());

    public ConstraintEdit ToEdit() => new(
        Before.Name, OracleIdentifier.Normalize(Name), Before.Type == ConstraintType.Check ? Condition : null,
        Before.Type == ConstraintType.ForeignKey && DeleteRule.Length > 0 ? DeleteRule : null, RenameIndex);

    /// <summary>Takes over what the user entered in <paramref name="other"/>, keeping <see cref="Before"/>.</summary>
    public void CopyFrom(ExistingConstraint other)
    {
        ArgumentNullException.ThrowIfNull(other);
        (Name, Condition, DeleteRule, RenameIndex) = (other.Name, other.Condition, other.DeleteRule, other.RenameIndex);
    }
}
