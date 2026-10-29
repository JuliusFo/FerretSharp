using System.Globalization;
using FerretSharp.Core.Audit;
using FerretSharp.Core.Design;
using FerretSharp.Core.Oracle;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Schema;
using FerretSharp.UI.Resources;

namespace FerretSharp.UI.State;

/// <summary>A constraint the edit mode adds, as its fields show it.</summary>
public sealed class DesignConstraint
{
    private readonly SuggestedName _name = new();

    public ConstraintType Type { get; set; } = ConstraintType.Unique;

    /// <summary>The name as the field shows it. Setting it is typing it: it no longer follows type and columns – empty follows again.</summary>
    public string Name
    {
        get => _name.Value;
        set => _name.Type(value);
    }

    /// <summary>The suggested name for its type and columns, unless the user typed one (<see cref="TableDesignerDraft.SuggestName"/>).</summary>
    internal void Suggest(string name) => _name.Follow(name);

    public List<string> Columns { get; } = [];

    public string Condition { get; set; } = "";

    public TableRef? References { get; set; }

    /// <summary>Referenced columns, one per entry of <see cref="Columns"/>.</summary>
    public List<string> ReferencedColumns { get; } = [];

    /// <summary><c>CASCADE</c>, <c>SET NULL</c> or empty for none.</summary>
    public string DeleteRule { get; set; } = "";

    public bool WithIndex { get; set; } = true;

    /// <summary>The referenced column for the key column at <paramref name="at"/> (a foreign key); missing entries before it stay empty.</summary>
    public void SetReferenced(int at, string? column)
    {
        while (ReferencedColumns.Count <= at)
        {
            ReferencedColumns.Add("");
        }

        ReferencedColumns[at] = column ?? "";
    }

    public ConstraintDraft ToDraft() => new(
        OracleIdentifier.Normalize(Name), Type, [.. Columns], Type == ConstraintType.Check ? Condition : null,
        Type == ConstraintType.ForeignKey ? References : null, Type == ConstraintType.ForeignKey ? [.. ReferencedColumns] : null,
        DeleteRule.Length > 0 ? DeleteRule : null, WithIndex);
}
