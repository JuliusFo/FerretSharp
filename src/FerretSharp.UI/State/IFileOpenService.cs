namespace FerretSharp.UI.State;

/// <summary>A file the user picked; <see cref="Content"/> is null if it is larger than the caller allowed.</summary>
public sealed record OpenedFile(string Name, long Length, byte[]? Content);

/// <summary>Native "Open" dialog; implemented by the host (WPF), the UI stays platform-neutral.</summary>
public interface IFileOpenService
{
    /// <param name="filter">Dialog filter label, e.g. <c>Textdateien</c>.</param>
    /// <param name="extensions">Offered extensions without dot, e.g. <c>txt</c>; all files are always offered too.</param>
    /// <param name="maxBytes">Files larger than this are not read (<see cref="OpenedFile.Content"/> null).</param>
    /// <returns>The file, or null if the user cancelled.</returns>
    Task<OpenedFile?> OpenAsync(string filter, IReadOnlyList<string> extensions, long maxBytes);
}
