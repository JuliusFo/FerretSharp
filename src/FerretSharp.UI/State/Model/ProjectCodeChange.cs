using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Design;
using FerretSharp.Core.Schema;

namespace FerretSharp.UI.State;

/// <summary>What the C# project should get (WP-41).</summary>
public abstract record CodeChangeRequest
{
    /// <summary>A designer change of a mapped table: the table as it was and the draft that ran (or is about to).</summary>
    public sealed record ForTable(TableDetails Before, TableDraft Draft) : CodeChangeRequest
    {
        public IReadOnlyList<ColumnChange> Changes { get; } = ColumnChanges.Of(Before, Draft);
    }

    /// <summary>A table without entity: entity and configuration as new files.</summary>
    /// <param name="EntityFolder">The folder the user chose for the entity (relative to the work clone); null for the most used.</param>
    /// <param name="ConfigurationFolder">The same for the configuration.</param>
    public sealed record ForNewEntity(TableDetails Table, IReadOnlyList<ForeignKeyInfo> Outgoing, string Name, string? EntityFolder = null, string? ConfigurationFolder = null)
        : CodeChangeRequest;

    /// <summary>Whether the table change touches the project at all (a comment or a constraint does not).</summary>
    public bool HasChanges => this is not ForTable table || table.Changes.Count > 0;
}

/// <summary>Where a code change stands.</summary>
public enum CodeChangeStage
{
    /// <summary>No project linked or no work clone chosen for the group: nothing is looked at.</summary>
    NoWorkClone,

    /// <summary>The work clone does not hold the linked project (<see cref="ProjectCodeChange.Fit"/>).</summary>
    WorkCloneDoesNotFit,

    /// <summary>
    /// The work clone is the linked clone of a connection of the group: it must match its database and is pulled – never
    /// written into (decision of the user; review WP-41: it was taken, without even a warning).
    /// </summary>
    WorkCloneIsLinked,

    Planning,
    Planned,
    Writing,

    /// <summary>Written (see <see cref="ProjectCodeChange.Results"/>, a file may have been refused).</summary>
    Written,

    /// <summary>Reading the work clone failed (<see cref="ProjectCodeChange.Error"/>).</summary>
    Failed,
}

/// <summary>
/// One preview of a code change (WP-41, ADR 0050): reads the group's work clone, plans the change and checks the target in
/// the background, writes on request. Never the linked clone of a connection – only the work clone, and only one that holds
/// the linked project at the same place (decision of the user).
/// </summary>
/// <param name="linkedProjects">The projects linked to the connections of the group, for the branch warning.</param>
public sealed class ProjectCodeChange(ClrModelMapping mapping, ClrProjectLink? link, string? workClone, IReadOnlyList<string> linkedProjects, CodeChangeRequest request)
{
    private readonly Lock _lock = new();

    public CodeChangeRequest Request => request;

    public CodeChangeStage Stage { get; private set; } = CodeChangeStage.Planning;

    public WorkCloneFit? Fit { get; private set; }

    public string? WorkClone => workClone;

    public CodeChangePlan? Plan { get; private set; }

    public CodeTargetCheck? Target { get; private set; }

    public IReadOnlyList<FileWriteResult>? Results { get; private set; }

    public string? Error { get; private set; }

    /// <summary>Planned, something to write and nothing against it.</summary>
    public bool CanWrite => Stage == CodeChangeStage.Planned && Plan is { CanWrite: true };

    public event Action? Changed;

    /// <summary>Reads the work clone and plans the change; the file system is read off the caller's thread.</summary>
    public async Task PlanAsync(CancellationToken cancellationToken)
    {
        if (link is null || workClone is null)
        {
            Set(CodeChangeStage.NoWorkClone);
            return;
        }

        Set(CodeChangeStage.Planning);
        try
        {
            // The file system and git off the render thread – a linked clone may lie on a share (review WP-41).
            var (stage, fit, plan, target) = await Task.Run(async () =>
            {
                var fits = WorkCloneMapping.Check(link.ProjectFile, workClone);
                if (fits != WorkCloneFit.Fits)
                {
                    return (CodeChangeStage.WorkCloneDoesNotFit, fits, (CodeChangePlan?)null, (CodeTargetCheck?)null);
                }

                if (IsLinkedClone(workClone, linkedProjects.Append(link.ProjectFile)))
                {
                    return (CodeChangeStage.WorkCloneIsLinked, fits, null, null);
                }

                var planned = Planned(cancellationToken);
                var checkedTarget = planned.Files.Count > 0 ? await CodeTarget.CheckAsync(planned, linkedProjects, cancellationToken) : null;
                return (CodeChangeStage.Planned, fits, planned, checkedTarget);
            }, cancellationToken);
            (Fit, Plan, Target) = (fit, plan, target);
            Set(stage);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = ex.Message;
            Set(CodeChangeStage.Failed);
        }
    }

    /// <summary>Writes the planned files; a refused file is in <see cref="Results"/>, nothing else of the plan then.</summary>
    public async Task WriteAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (!CanWrite)
            {
                return;
            }

            Stage = CodeChangeStage.Writing;
        }

        Changed?.Invoke();
        try
        {
            Results = await Task.Run(() => CodeChangeWriter.WriteAsync(Plan!, cancellationToken), cancellationToken);
            Set(CodeChangeStage.Written);
        }
        catch (OperationCanceledException)
        {
            Set(CodeChangeStage.Planned);
            throw;
        }
    }

    /// <summary>The folder is the working copy of one of the linked projects (the same Git root).</summary>
    internal static bool IsLinkedClone(string workClone, IEnumerable<string> linkedProjects)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(GitFolder.FindRoot(workClone) ?? workClone));
        return linkedProjects.Select(GitFolder.FindRoot).OfType<string>()
            .Any(r => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(r)), root, StringComparison.OrdinalIgnoreCase));
    }

    private CodeChangePlan Planned(CancellationToken cancellationToken)
    {
        switch (request)
        {
            case CodeChangeRequest.ForTable table:
                if (mapping.EntityOf(table.Before.Table.Ref)?.Entity is not { } entity)
                {
                    return new CodeChangePlan(workClone!, [], [], [Core.Resources.CodeEditText.Problem_NoEntity]);
                }

                var scan = CodeScan.Read(workClone!, EntityCodeChanges.Words(entity), cancellationToken);
                return EntityCodeChanges.Plan(mapping, table.Before.Table.Ref, table.Changes, scan);
            case CodeChangeRequest.ForNewEntity created:
                var name = created.Name.Trim().Length > 0 ? created.Name.Trim() : created.Table.Table.Name;
                var all = CodeScan.Read(workClone!, NewEntityCode.Words(mapping.Model, name), cancellationToken);
                return NewEntityCode.Plan(mapping, created.Table, created.Outgoing, name, all, created.EntityFolder, created.ConfigurationFolder);
            default:
                throw new InvalidOperationException($"Unknown request {request.GetType().Name}.");
        }
    }

    private void Set(CodeChangeStage stage)
    {
        lock (_lock)
        {
            Stage = stage;
        }

        Changed?.Invoke();
    }
}
