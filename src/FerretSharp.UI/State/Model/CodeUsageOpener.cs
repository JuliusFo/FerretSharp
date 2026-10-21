using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Settings;
using FerretSharp.UI.Resources;

namespace FerretSharp.UI.State;

/// <summary>
/// Opens a use in the editor (WP-40, ADR 0030): the file in the group's work clone (or the linked clone), the Visual
/// Studio instance by the solution at the same place, and a notice if that did not work out as planned.
/// </summary>
public sealed class CodeUsageOpener(ICodeEditorLauncher launcher, WorkClones workClones, AppSettingsService settings)
{
    /// <summary>The source file of the use as it would open now; null if no file exists.</summary>
    public SourceLocation? Locate(CodeUse use, ClrProjectLink link, string? group, CancellationToken cancellationToken) =>
        use.Document is null ? null : WorkCloneMapping.Resolve(use.Document, link.ProjectFile, workClones.ActiveFor(group), use.CallerType, cancellationToken);

    /// <returns>A notice for the user (opened elsewhere, failed, no file); null if it opened as planned.</returns>
    public async Task<string?> OpenAsync(CodeUse use, ClrProjectLink link, string? solution, string? group, CancellationToken cancellationToken)
    {
        if (use.Document is null)
        {
            return ModelViewText.Usage_NoSourceFile;
        }

        // The mapping of the solution reads the file system too (Git root, files): with the location off the render thread
        // (review 3.44.0, A24).
        var (location, vsSolution) = await Task.Run(() =>
        {
            var found = Locate(use, link, group, cancellationToken);
            var workClone = workClones.ActiveFor(group);
            return (found, found is null || solution is null ? null : WorkCloneMapping.MapSolution(solution, link.ProjectFile, found.InWorkClone ? workClone : null));
        }, cancellationToken);
        if (location is null)
        {
            return TextFormat.Format(ModelViewText.Usage_FileMissing, use.Document);
        }

        var result = await launcher.OpenAsync(settings.Current.CodeEditor, location.Path, location.ByType ? null : use.Line, vsSolution, cancellationToken);
        return result.Outcome switch
        {
            CodeOpenOutcome.Failed => result.Message ?? ModelViewText.Usage_OpenFailed,
            CodeOpenOutcome.OpenedElsewhere => ModelViewText.Usage_OpenedElsewhere,
            _ => location.ByType ? ModelViewText.Usage_OpenedByType : null,
        };
    }
}
