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
/// <param name="SavedHash">
/// The tab's text when the file was last read or written (<see cref="BodyHash"/>): the tab shows "●" when its own differs.
/// With <see cref="HeaderInText"/> the text the tab showed (<see cref="ScriptHeaders.EditorText"/>), else the SQL below the header.
/// </param>
/// <param name="Header">The file's header: written back with the parameters of the tab's text.</param>
public sealed record ScriptLink(ScriptRootKind Root, string? Group, string Path, string FileHash, string SavedHash, ScriptHeader Header)
{
    /// <summary>
    /// The tab's text holds the file's targets and the lines FerretSharp keeps as they are (fix-script-header-in-editor):
    /// saving takes them from the text. False for a link of a workspace written before – saving keeps the file's lines.
    /// </summary>
    public bool HeaderInText { get; init; }

    /// <summary>The script's name: the tab's title.</summary>
    public string Name => ScriptPaths.NameOf(Path);

    /// <summary>A link to the file just read or written, for a tab showing <paramref name="text"/> (<see cref="ScriptHeaders.EditorText"/>).</summary>
    public static ScriptLink To(ScriptRoot root, string path, ScriptHeader header, string text, string fileHash) =>
        new(root.Kind, root.Group, path, fileHash, BodyHash(text), header) { HeaderInText = true };

    /// <summary>
    /// All parts there: a link from a workspace file edited by hand may lack its path or header (System.Text.Json leaves
    /// them null) – the tab is restored without it (review WP-77, K2).
    /// </summary>
    public bool IsComplete =>
        Path is not null && FileHash is not null && SavedHash is not null
        && Header is { Parameters: not null, Other: not null } && Header.Parameters.All(p => p?.Name is not null)
        && Header.Other.All(line => line is not null)
        && Header.Targets is not null && Header.Targets.All(t => t is { Parameter: not null, Table: not null, Column: not null });

    /// <summary>What saving the tab's text writes into this file (<see cref="ScriptHeaders.ForSave"/>).</summary>
    public ScriptSave SaveOf(string text, IReadOnlyList<SqlVariable> variables) => ScriptHeaders.ForSave(text, variables, Header, HeaderInText);

    /// <summary>
    /// The tab's text differs from the file as last read or written, or a parameter of the file has a variable of another
    /// type now. The order and spelling of the parameters do not count, nor binds the header does not name (a file
    /// without header, from VS Code): those differ from what saving would write, but the user changed nothing (review
    /// WP-77, M1).
    /// </summary>
    public bool IsChanged(string text, IReadOnlyList<SqlVariable> variables) =>
        BodyHash(text) != SavedHash
        || Header.Parameters.Any(p => variables.FirstOrDefault(v => string.Equals(v.Name, p.Name, StringComparison.OrdinalIgnoreCase)) is { } v && v.Type != p.Type);

    /// <summary>The hash of a tab's text or the SQL below a header, line breaks ignored.</summary>
    public static string BodyHash(string body) => ScriptFiles.HashOf(body);
}
