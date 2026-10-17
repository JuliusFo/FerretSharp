namespace FerretSharp.UI.State;

/// <summary>An entry of <c>MoreMenu</c> (a "⋯" button or a small menu of plain actions).</summary>
public sealed record MoreMenuItem(string Label, Func<Task> Run, bool Enabled = true)
{
    public MoreMenuItem(string label, Action run, bool enabled = true)
        : this(label, () =>
        {
            run();
            return Task.CompletedTask;
        }, enabled)
    {
    }

    /// <summary>The main action of the menu, in bold (R6b, B4).</summary>
    public bool Strong { get; init; }

    /// <summary>An action that deletes or cannot be taken back, in red.</summary>
    public bool Danger { get; init; }

    /// <summary>A line above the entry.</summary>
    public bool SeparatorBefore { get; init; }

    public string? Title { get; init; }
}
