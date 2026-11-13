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

/// <summary>The entries of a save dialog (<see cref="ScriptSaveDraft.Inputs"/>).</summary>
/// <param name="TextDescription">The description the text gave the dialog when it opened; one typed into the text since then wins.</param>
public sealed record ScriptSaveInputs(ScriptRoot Root, string Name, string Folder, string Description, string? TextDescription);

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
        Save = request.Link?.SaveOf(request.Text, request.Variables) ?? ScriptHeaders.ForSave(request.Text, request.Variables, null, false);
        Name = request.Name;
        // A description typed at the top of the text, else the file's.
        Description = Save.Header.Description ?? "";
        if (request.Link is { } link)
        {
            Root = ScriptRoots.Find(roots, link.Root, link.Group) ?? roots[0];
            Folder = Root.Is(link.Root, link.Group) ? ScriptPaths.FolderOf(link.Path) : "";
        }
        else
        {
            Root = roots[0];
        }
    }

    public ScriptSaveRequest Request { get; }

    /// <summary>
    /// Header, body and the tab afterwards, from the text's header lines and the types of its variables – worked out once,
    /// not on every keystroke (review WP-77, K8).
    /// </summary>
    public ScriptSave Save { get; }

    /// <summary>The binds of the text with their types.</summary>
    public IReadOnlyList<ScriptParameter> Parameters => Save.Header.Parameters;

    /// <summary>The folders of the settings the connection sees: personal, its group's, general.</summary>
    public IReadOnlyList<ScriptRoot> Roots { get; }

    public ScriptRoot Root { get; set; }

    public string Name { get; set; }

    /// <summary>Below <see cref="Root"/>, parts separated by <c>/</c> or <c>\</c>; empty for the root itself.</summary>
    public string Folder { get; set; } = "";

    public string Description { get; set; } = "";

    /// <summary>What was entered (WP-89: kept while the help is read, <see cref="Take"/>).</summary>
    public ScriptSaveInputs Inputs => new(Root, Name, Folder, Description, Save.Header.Description);

    /// <summary>
    /// Takes the entries of an earlier dialog of the same tab: the name; the description unless the text has another one
    /// now (typed after reading the help – review WP-89, N1); the folder of the settings with the folder below it, only if
    /// this connection still sees it (a subfolder belongs to its root – N5).
    /// </summary>
    public void Take(ScriptSaveInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        Name = inputs.Name;
        if (Save.Header.Description == inputs.TextDescription)
        {
            Description = inputs.Description;
        }

        if (Roots.Contains(inputs.Root))
        {
            (Root, Folder) = (inputs.Root, inputs.Folder);
        }
    }

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
    /// The header to write: the description as typed in the dialog, the parameters of the text's binds with their types,
    /// the targets (WP-84) and the lines FerretSharp keeps as they are – from the top of the text (<see cref="ScriptHeaders.ForSave"/>).
    /// </summary>
    public ScriptHeader Header => Save.Header with { Description = string.IsNullOrWhiteSpace(Description) ? null : Description.Trim() };
}
