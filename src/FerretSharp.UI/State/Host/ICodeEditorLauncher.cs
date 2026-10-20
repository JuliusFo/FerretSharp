using FerretSharp.Core.Settings;

namespace FerretSharp.UI.State;

public enum CodeOpenOutcome
{
    /// <summary>Opened where it should: the Visual Studio instance with the solution, VS Code, or the system's program.</summary>
    Opened,

    /// <summary>No Visual Studio instance had the solution or the file open: opened with <c>devenv /Edit</c> instead.</summary>
    OpenedElsewhere,

    /// <summary>Nothing could open it (no Visual Studio or VS Code installed, the program failed).</summary>
    Failed,
}

/// <param name="Message">Why it failed; null otherwise.</param>
public sealed record CodeOpenResult(CodeOpenOutcome Outcome, string? Message = null);

/// <summary>
/// Opens a source file at a line in the user's editor (WP-40, ADR 0030); implemented by the host: Visual Studio through the
/// Running Object Table (the instance with <c>solution</c> open), VS Code, or the system's program.
/// </summary>
public interface ICodeEditorLauncher
{
    /// <param name="line">1-based; null opens the file only.</param>
    /// <param name="solution">The solution whose Visual Studio instance should open it (in the work clone); null = any instance.</param>
    Task<CodeOpenResult> OpenAsync(CodeEditor editor, string file, int? line, string? solution, CancellationToken cancellationToken);
}
