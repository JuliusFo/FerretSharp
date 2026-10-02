namespace FerretSharp.UI.State;

/// <summary>Native "Save as" dialog; implemented by the host (WPF), the UI stays platform-neutral.</summary>
public interface IFileSaveService
{
    /// <param name="fileName">Suggested file name, e.g. <c>KUNDEN.csv</c>.</param>
    /// <param name="filter">Dialog filter label, e.g. <c>CSV (Semikolon)</c>.</param>
    /// <param name="utf8Bom">Write a byte order mark (Excel needs it to read UTF-8 CSV).</param>
    /// <returns>The path written, or null if the user cancelled.</returns>
    Task<string?> SaveTextAsync(string fileName, string filter, string text, bool utf8Bom);
}
