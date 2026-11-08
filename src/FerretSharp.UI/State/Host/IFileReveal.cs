namespace FerretSharp.UI.State;

/// <summary>Shows a file in the system's file manager, selected (WP-77: a script in the explorer); implemented by the host.</summary>
public interface IFileReveal
{
    /// <summary>Opens the file's folder with the file selected; does nothing if the file manager cannot be started.</summary>
    void Reveal(string path);
}
