using FerretSharp.Core.Ddl;
using FerretSharp.Core.Design;
using FerretSharp.Core.Oracle;
using FerretSharp.Core.Schema;

namespace FerretSharp.UI.State;

/// <summary>
/// The draft of a new index in the "Indexes" view (WP-23, stage 1; out of <c>IndexesView</c> since R4b): its columns in
/// order, UNIQUE, and a name that follows the columns until the user types one (<see cref="SuggestedName"/>). Plain state –
/// <see cref="IndexDesign"/> checks the draft and writes its DDL.
/// </summary>
/// <param name="indexes">The table's indexes: their names are taken.</param>
public sealed class IndexDraftEditor(TableRef table, IReadOnlyList<IndexInfo> indexes)
{
    private readonly SuggestedName _name = new();
    private readonly List<string> _columns = [];

    /// <summary>An empty draft, or one prepared for a foreign key without index (its columns).</summary>
    public static IndexDraftEditor Start(TableRef table, IReadOnlyList<IndexInfo> indexes, IndexDraft? prepared)
    {
        var editor = new IndexDraftEditor(table, indexes) { Unique = prepared?.Unique ?? false };
        editor._columns.AddRange(prepared?.Columns ?? []);
        editor.SuggestName();
        return editor;
    }

    public IReadOnlyList<string> Columns => _columns;

    public bool Unique { get; set; }

    /// <summary>The name as the field shows it.</summary>
    public string Name => _name.Value;

    /// <summary>The name as Oracle stores it: typed without quotes upper case, in quotes as typed.</summary>
    public IndexDraft Draft => new(OracleIdentifier.Normalize(Name), [.. _columns], Unique);

    /// <summary>The user typed a name: it no longer follows the columns – empty follows them again.</summary>
    public void TypeName(string? value) => _name.Type(value);

    /// <summary><c>IX_&lt;TABLE&gt;_&lt;COLUMNS&gt;</c> for the columns as they are, unless the user typed a name.</summary>
    public void SuggestName() => _name.Follow(_columns.Count == 0 ? "" : IndexDesign.SuggestName(table.Name, _columns, indexes.Select(i => i.Name)));

    /// <summary>A column at the end of the index.</summary>
    public void Add(string? column)
    {
        if (!string.IsNullOrEmpty(column) && !_columns.Contains(column))
        {
            _columns.Add(column);
            SuggestName();
        }
    }

    public void Remove(int at)
    {
        _columns.RemoveAt(at);
        SuggestName();
    }

    /// <summary>Moves the column at <paramref name="at"/> by one position: -1 earlier, 1 later.</summary>
    public void Move(int at, int by)
    {
        (_columns[at], _columns[at + by]) = (_columns[at + by], _columns[at]);
        SuggestName();
    }

    /// <summary>The CREATE INDEX, as soon as there is something to write (problems keep it from running).</summary>
    public IReadOnlyList<DdlStep> Steps()
    {
        var draft = Draft;
        return draft.Columns.Count > 0 && draft.Name.Length > 0 && !draft.Name.Contains('"', StringComparison.Ordinal) ? [IndexDesign.Step(draft, table)] : [];
    }
}
