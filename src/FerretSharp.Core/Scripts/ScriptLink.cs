using FerretSharp.Core.Query;

namespace FerretSharp.Core.Scripts;

/// <summary>
/// The script file a SQL tab was opened from or saved to (WP-77): kept in the workspace as root and relative path, never
/// the absolute path – the folder of a group may lie elsewhere on a colleague's machine, and a path that leads out of the
/// root is refused when it is used (<see cref="ScriptPaths.Resolve"/>).
/// </summary>
/// <param name="Root">Which folder.</param>
/// <param name="Group">The group of a <see cref="ScriptRootKind.Group"/> folder, as the settings name it.</param>
/// <param name="Path">Relative to the folder, with <c>/</c>.</param>
/// <param name="FileHash">The file's bytes when last read or written: a save over a file changed since asks first.</param>
/// <param name="SavedHash">The script as the file holds it (<see cref="ContentHash"/>): the tab shows "●" when its own differs.</param>
/// <param name="Header">The file's header: written back with the parameters of the tab's text.</param>
public sealed record ScriptLink(ScriptRootKind Root, string? Group, string Path, string FileHash, string SavedHash, ScriptHeader Header)
{
    /// <summary>The script's name: the tab's title.</summary>
    public string Name => ScriptPaths.NameOf(Path);

    /// <summary>A link to the file just read or written.</summary>
    public static ScriptLink To(ScriptRoot root, string path, ScriptHeader header, string body, string fileHash) =>
        new(root.Kind, root.Group, path, fileHash, ContentHash(header, body), header);

    /// <summary>The header to write for the tab's text: the file's, with the parameters of the text's binds.</summary>
    public ScriptHeader HeaderFor(string text, IReadOnlyList<SqlVariable> variables) =>
        Header with { Parameters = ScriptHeaders.ParametersOf(text, variables) };

    /// <summary>The tab's text or the types of its parameters differ from the file as last read or written.</summary>
    public bool IsChanged(string text, IReadOnlyList<SqlVariable> variables) =>
        ContentHash(HeaderFor(text, variables), text) != SavedHash;

    /// <summary>The hash of a script's content, line breaks ignored.</summary>
    public static string ContentHash(ScriptHeader header, string body) => ScriptFiles.HashOf(ScriptHeaders.Compose(header, body));
}
