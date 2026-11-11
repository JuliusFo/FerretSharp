using FerretSharp.Core.Connections;
using FerretSharp.Core.Query;
using FerretSharp.Core.Scripts;

namespace FerretSharp.UI.State;

/// <summary>What is saved as a script (WP-77): the window, the selection or a statement of the history.</summary>
public enum ScriptSaveSource
{
    /// <summary>The whole tab; the tab is linked to the file afterwards ("Save as" of a linked tab too).</summary>
    Window,

    /// <summary>The selected text; the tab stays as it is.</summary>
    Selection,

    /// <summary>A statement of the history; no tab involved.</summary>
    History,
}

/// <summary>A request to save as a script: the text with its variables, and where it comes from.</summary>
/// <param name="Link">The file the tab is linked to, for "Save as" (root and folder preset, description kept).</param>
public sealed record ScriptSaveRequest(ScriptSaveSource Source, string Text, IReadOnlyList<SqlVariable> Variables, string Name, ScriptLink? Link = null);

/// <summary>
/// The fields of the "Save as script" dialog (WP-77): name, folder of the settings, folder below it, description. Checks
/// name and folder as the file system will (<see cref="ScriptPaths"/>); the dialog shows the errors at the fields.
/// </summary>
public sealed class ScriptSaveDraft
{
    public ScriptSaveDraft(ScriptSaveRequest request, IReadOnlyList<ScriptRoot> roots)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(roots);
        if (roots.Count == 0)
        {
            throw new ArgumentException("There is always the personal folder.", nameof(roots));
        }

        Request = request;
        Roots = roots;
        Parameters = ScriptHeaders.ParametersOf(request.Text, request.Variables, request.Link?.Header.Parameters);
        Name = request.Name;
        if (request.Link is { } link)
        {
            Root = ScriptRoots.Find(roots, link.Root, link.Group) ?? roots[0];
            Folder = Root.Is(link.Root, link.Group) ? ScriptPaths.FolderOf(link.Path) : "";
            Description = link.Header.Description ?? "";
        }
        else
        {
            Root = roots[0];
        }
    }

    public ScriptSaveRequest Request { get; }

    /// <summary>The binds of the text with the types of its variables – worked out once, not on every keystroke (review WP-77, K8).</summary>
    public IReadOnlyList<ScriptParameter> Parameters { get; }

    /// <summary>The folders of the settings the connection sees: personal, its group's, general.</summary>
    public IReadOnlyList<ScriptRoot> Roots { get; }

    public ScriptRoot Root { get; set; }

    public string Name { get; set; }

    /// <summary>Below <see cref="Root"/>, parts separated by <c>/</c> or <c>\</c>; empty for the root itself.</summary>
    public string Folder { get; set; } = "";

    public string Description { get; set; } = "";

    /// <summary>Why the name cannot be used; null if it can.</summary>
    public string? NameError => ScriptPaths.NameProblem(Name.Trim());

    /// <summary>Why the folder cannot be used; null if it can.</summary>
    public string? FolderError
    {
        get
        {
            try
            {
                ScriptPaths.Folder(Folder);
                return null;
            }
            catch (RefusedException ex)
            {
                return ex.Message;
            }
        }
    }

    public bool CanSave => NameError is null && FolderError is null;

    /// <summary>The script's path below <see cref="Root"/>.</summary>
    public string Path => ScriptPaths.Of(ScriptPaths.Folder(Folder), Name.Trim());

    /// <summary>
    /// The header to write: the description as typed, the parameters of the text's binds with the types of its variables,
    /// and for "Save as" the targets (WP-84) and the lines of the linked file FerretSharp does not know.
    /// </summary>
    public ScriptHeader Header => new(
        string.IsNullOrWhiteSpace(Description) ? null : Description.Trim(),
        Parameters,
        Request.Link?.Header.Other ?? [])
    {
        Targets = Request.Link?.Header.Targets ?? [],
    };
}
