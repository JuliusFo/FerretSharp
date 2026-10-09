using System.Globalization;
using FerretSharp.Core.ClrModel;
using FerretSharp.UI.Resources;
using Microsoft.JSInterop;

namespace FerretSharp.UI.State;

/// <summary>
/// What grid.js needs to know about a column: header (name, other name and C# type, Oracle type, badges), sorting,
/// pinning, editing and the member list. Ids are <c>c</c> + schema index (Oracle names may contain dots).
/// </summary>
public sealed record GridColumn(string Id, string Name, string Label, string Type, string Category, bool Nullable)
{
    /// <summary>The other name (C# property or column) and the C# type, on the header's middle line (WP-12).</summary>
    public string? Alternate { get; init; }

    public string? ClrType { get; init; }

    /// <summary>The table has the middle line at all.</summary>
    public bool ClrLine { get; init; }

    public bool Pk { get; init; }

    /// <summary>A declared FK; <see cref="FkModel"/>: a relationship of the C# model without a constraint.</summary>
    public bool Fk { get; init; }

    public bool FkModel { get; init; }

    /// <summary>Right-aligned with grouped digits (numbers shown as numbers, not as enum members).</summary>
    public bool Numeric { get; init; }

    public bool Sortable { get; init; }

    public bool Pinned { get; init; }

    public string? Comment { get; init; }

    /// <summary>Whether the cell can be edited at all; whether the workspace may write is switched in grid.js (setEditable).</summary>
    public bool Editable { get; init; }

    /// <summary>The same for new rows (a key without default may be filled there).</summary>
    public bool EditableNew { get; init; }

    /// <summary>Opens the LOB editor instead (also to read the whole value on a locked workspace).</summary>
    public bool Lob { get; init; }

    /// <summary>Long texts (and their line breaks) are edited in a larger box.</summary>
    public bool Multiline { get; init; }

    /// <summary>Enum and converted bool columns: the members to pick from, and why a value without a member is marked.</summary>
    public IReadOnlyList<ValueOption>? Options { get; init; }

    public string? UnknownText { get; init; }

    public static string IdOf(int index) => "c" + index.ToString(CultureInfo.InvariantCulture);

    public static int IndexOf(string id) => int.Parse(id.AsSpan(1), CultureInfo.InvariantCulture);
}

/// <summary>How grid.js builds the grid.</summary>
/// <param name="FirstRow">Rough scroll position to restore (0 = top).</param>
/// <param name="Editable">The workspace may write (v2); the columns say whether they can be edited at all.</param>
/// <param name="HeaderHeight">With the line for C# names (WP-12); null for the theme default.</param>
/// <param name="Table">A table tab: header menu, Del, pinning and scroll position are reported to .NET. A query result
/// (SQL editor, LINQ console) has none of these and only answers GetRows, OnCopy and OnCellContextMenu.</param>
public sealed record GridOptions(IReadOnlyList<GridSort> Sorts, int FirstRow, bool Editable, int? HeaderHeight, bool Table)
{
    /// <summary>The texts grid.js shows itself, in the UI language at the time the grid is built (WP-29).</summary>
    public GridTexts Texts { get; init; } = new();
}

/// <summary>
/// Texts that grid.js shows itself (header tooltips, the member list's editor): JS holds no texts, they come from the
/// resources, read when the options are created – not cached in a static field, so they follow the UI language.
/// </summary>
public sealed record GridTexts
{
    /// <summary>Tooltip of the FK badge for a relationship of the C# model.</summary>
    public string FkModel { get; init; } = GridText.Grid_FkModelBadge;

    /// <summary>Header tooltip of a column whose type cannot be sorted.</summary>
    public string NotSortable { get; init; } = GridText.Grid_NotSortable;

    /// <summary>Option of a value that is no enum member; <c>{0}</c> is the value (replaced in grid.js).</summary>
    public string NoMember { get; init; } = GridText.Grid_ValueNoMember;
}

/// <summary>A sorted column as AG Grid reports it ("asc"/"desc").</summary>
public sealed record GridSort(string ColId, string Sort);

/// <param name="Failed">The block could not be read (the footer says why): grid.js fails it instead of an exception
/// crossing the interop boundary, which Blazor would leave unobserved.</param>
public sealed record GridPage(IReadOnlyList<Dictionary<string, object?>> Rows, int LastRow, bool Failed = false)
{
    public static readonly GridPage Failure = new([], -1, true);
}

/// <summary>Result of an edit for grid.js: the redrawn row, or the message to show in the reopened editor.</summary>
public sealed record GridEdit(Dictionary<string, object?>? Row, string? Error);

/// <summary>A loaded row to redraw after deleting/reverting, by row index.</summary>
public sealed record GridRowUpdate(int RowIndex, Dictionary<string, object?> Row);

/// <summary>
/// The JS side of a grid (grid.js, AG Grid) for <c>FerretGrid</c> and <c>SqlResultGrid</c> (R2: was in both): loads the
/// module, creates and destroys the grid, and calls it only while the component lives – a call after
/// <see cref="DisposeAsync"/> (a handler that waited while the tab closed) does nothing.
/// </summary>
public sealed class GridBridge(IJSRuntime js, string elementId, IDisposable selfReference) : IAsyncDisposable
{
    private IJSObjectReference? _module;
    private readonly DomModule _dom = new(js);
    private bool _disposed;

    /// <summary>The grid was created and the component still lives.</summary>
    public bool IsCreated { get; private set; }

    /// <summary>Loads grid.js; false if the component was disposed meanwhile (then there is nothing to show).</summary>
    public async Task<bool> LoadAsync()
    {
        if (_module is null && !_disposed)
        {
            var module = await js.InvokeAsync<IJSObjectReference>("import", "./_content/FerretSharp.UI/js/grid.js");
            if (_disposed)
            {
                // Disposed while the module loaded: DisposeAsync found no module.
                await DisposeQuietlyAsync(module);
                return false;
            }

            _module = module;
        }

        return !_disposed;
    }

    /// <summary>Builds the grid (again); grid.js calls back the component through its reference.</summary>
    public async Task CreateAsync(IReadOnlyList<GridColumn> columns, GridOptions options)
    {
        if (await LoadAsync() && await CallAsync("create", selfReference, columns, options))
        {
            IsCreated = true;
        }
    }

    /// <summary>
    /// Calls an export of grid.js for this grid (the element id goes first); false if not loaded or disposed (also while
    /// the call ran). Pass a list, not an array, as a single argument: an array of a reference type would become the
    /// params array itself (array covariance), and grid.js got one row instead of the list (LOB editor since 3.6.1,
    /// found in WP-21).
    /// </summary>
    public async Task<bool> CallAsync(string identifier, params object?[] args)
    {
        if (_module is null || _disposed)
        {
            return false;
        }

        try
        {
            await _module.InvokeVoidAsync(identifier, [elementId, .. args]);
            return true;
        }
        catch (Exception ex) when (_disposed && ex is JSDisconnectedException or ObjectDisposedException)
        {
            return false; // disposed while the call ran (closing the tab, disconnecting)
        }
    }

    /// <summary>Copies text to the clipboard; false (with a note) if the clipboard is not available.</summary>
    public Task<bool> CopyTextAsync(ShellState shell, string text) => _dom.CopyTextAsync(shell, text);

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        IsCreated = false;
        if (_module is not null)
        {
            try
            {
                await _module.InvokeVoidAsync("destroy", elementId);
            }
            catch (Exception ex) when (ex is JSDisconnectedException or ObjectDisposedException)
            {
            }

            await DisposeQuietlyAsync(_module);
        }

        await _dom.DisposeAsync();
        selfReference.Dispose();
    }

    private static async Task DisposeQuietlyAsync(IJSObjectReference module)
    {
        try
        {
            await module.DisposeAsync();
        }
        catch (Exception ex) when (ex is JSDisconnectedException or ObjectDisposedException)
        {
        }
    }
}
