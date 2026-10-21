namespace FerretSharp.UI.State;

/// <summary>Native "Save as" dialog; implemented by the host (WPF), the UI stays platform-neutral.</summary>
public interface IFileSaveService
{
    /// <param name="fileName">Suggested file name, e.g. <c>KUNDEN.csv</c>.</param>
    /// <param name="filter">Dialog filter label, e.g. <c>CSV (semicolon)</c>.</param>
    /// <param name="utf8Bom">Write a byte order mark (Excel needs it to read UTF-8 CSV).</param>
    /// <param name="folder">Folder the dialog starts in (WP-23: the last script folder of the connection); null = the system's choice.</param>
    /// <returns>The path written, or null if the user cancelled.</returns>
    Task<string?> SaveTextAsync(string fileName, string filter, string text, bool utf8Bom, string? folder = null);

    /// <summary>Saves binary content (BLOB) as it is.</summary>
    /// <returns>The path written, or null if the user cancelled.</returns>
    Task<string?> SaveBytesAsync(string fileName, string filter, byte[] content);

    /// <summary>
    /// Only asks where to save (an export written afterwards, block by block: A13, WP-54). Writes nothing – an existing file
    /// the user agreed to replace stays until the export replaces it.
    /// </summary>
    /// <returns>The chosen path, or null if the user cancelled.</returns>
    Task<string?> AskPathAsync(string fileName, string filter);
}
