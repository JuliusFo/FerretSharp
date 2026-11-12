using FerretSharp.Core.Resources;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace FerretSharp.Core.ClrModel;

/// <summary>One file a change writes: a changed one with the hash it was read with, or a new one.</summary>
/// <param name="Relative">Its path below the work clone, as shown.</param>
/// <param name="Original">The file as read; null for a new file.</param>
/// <param name="Format">How it is written (for a changed file the one it has).</param>
public sealed record FileChange(string Path, string Relative, CodeFile? Original, string NewText, CodeFileFormat Format)
{
    private IReadOnlyList<DiffHunk>? _diff;

    public bool IsNew => Original is null;

    /// <summary>The changed lines with some around them.</summary>
    public IReadOnlyList<DiffHunk> Diff => _diff ??= LineDiff.Of(Original?.Text, NewText);
}

/// <summary>
/// What a designer change comes to in the C# project (WP-41, ADR 0050): the files with their new text, notes on what is
/// left to do by hand, and problems that keep anything from being written.
/// </summary>
/// <param name="Root">The work clone the paths lie below.</param>
public sealed record CodeChangePlan(string Root, IReadOnlyList<FileChange> Files, IReadOnlyList<string> Notes, IReadOnlyList<string> Problems)
{
    /// <summary>For a new entity: the folders it and its configuration can go to, and the ones chosen.</summary>
    public NewEntityPlaces? Places { get; init; }

    /// <summary>Something to write and nothing against it.</summary>
    public bool CanWrite => Files.Count > 0 && Problems.Count == 0;
}

/// <summary>
/// Folders (relative to the work clone) that hold entity classes and configuration classes, most used first, with the one
/// each new file goes to (WP-41: the most used, or the one the user chose).
/// </summary>
public sealed record NewEntityPlaces(IReadOnlyList<string> EntityFolders, string EntityFolder, IReadOnlyList<string> ConfigurationFolders, string ConfigurationFolder);

/// <summary>
/// The text changes collected for the files of a plan: insertions and removals at positions of the text as read. A change
/// keeps everything else of the file as it is – Roslyn finds the places, the text around them is not printed anew.
/// </summary>
internal sealed class FileEdits(string root)
{
    private readonly Dictionary<ScannedFile, List<TextChange>> _changes = [];
    private readonly List<FileChange> _created = [];

    public string Root => root;

    public bool IsEmpty => _changes.Values.All(c => c.Count == 0) && _created.Count == 0;

    public void Insert(ScannedFile file, int position, string text) => Add(file, new TextChange(new TextSpan(position, 0), text));

    public void Replace(ScannedFile file, TextSpan span, string text) => Add(file, new TextChange(span, text));

    public void Remove(ScannedFile file, TextSpan span) => Add(file, new TextChange(span, ""));

    /// <summary>
    /// Removes the lines of a node: from the start of its first line – with comments right above it – to the end of its last
    /// line. A blank line left on both sides goes with it, so no double blank line stays.
    /// </summary>
    public void RemoveLines(ScannedFile file, SyntaxNode node)
    {
        var text = file.File.Text;
        var start = LineStart(text, node.SpanStart);
        while (start > 0)
        {
            var above = LineStart(text, start - 1);
            var line = text[above..start].Trim();
            if (!line.StartsWith("//", StringComparison.Ordinal))
            {
                break;
            }

            start = above;
        }

        var end = LineEnd(text, node.Span.End);
        var below = end < text.Length ? text[end..LineEnd(text, end)].Trim() : "}";
        var blankAbove = start > 0 && text[LineStart(text, start - 1)..start].Trim().Length == 0;
        if (blankAbove && below.Length == 0)
        {
            end = LineEnd(text, end); // a blank line below as well as above: take one of them
        }
        else if (blankAbove && below.StartsWith('}'))
        {
            start = LineStart(text, start - 1); // no blank line before the closing brace
        }

        Remove(file, TextSpan.FromBounds(start, end));
    }

    /// <summary>The indentation of the line a position is on.</summary>
    public static string IndentAt(string text, int position)
    {
        var start = LineStart(text, position);
        var end = start;
        while (end < text.Length && text[end] is ' ' or '\t')
        {
            end++;
        }

        return text[start..end];
    }

    /// <summary>The position after the line break that ends the line of <paramref name="position"/>.</summary>
    public static int LineEnd(string text, int position)
    {
        var next = text.IndexOf('\n', position);
        return next < 0 ? text.Length : next + 1;
    }

    public static int LineStart(string text, int position)
    {
        var at = Math.Min(position, text.Length);
        while (at > 0 && text[at - 1] != '\n')
        {
            at--;
        }

        return at;
    }

    public void Create(FileChange file) => _created.Add(file);

    private void Add(ScannedFile file, TextChange change)
    {
        if (!_changes.TryGetValue(file, out var list))
        {
            _changes[file] = list = [];
        }

        list.Add(change);
    }

    /// <summary>
    /// The files with their new text. Changes that overlap, or a text that would have more syntax errors than before, are
    /// a problem: nothing of such a plan is written (a guard against a wrong edit, not a check of the user's code).
    /// </summary>
    public (IReadOnlyList<FileChange> Files, IReadOnlyList<string> Problems) Build()
    {
        var files = new List<FileChange>();
        var problems = new List<string>();
        foreach (var (file, changes) in _changes.OrderBy(c => c.Key.Relative, StringComparer.Ordinal))
        {
            if (changes.Count == 0)
            {
                continue;
            }

            // Stable: insertions at the same place keep the order they were made in.
            var ordered = changes.Select((c, i) => (Change: c, Index: i)).OrderBy(c => c.Change.Span.Start).ThenBy(c => c.Index).Select(c => c.Change).ToList();
            if (ordered.Zip(ordered.Skip(1)).Any(p => p.First.Span.End > p.Second.Span.Start))
            {
                problems.Add(TextFormat.Format(CodeEditText.Problem_Overlap, file.Relative));
                continue;
            }

            var text = SourceText.From(file.File.Text).WithChanges(ordered).ToString();
            if (text == file.File.Text)
            {
                continue;
            }

            if (CodeScan.SyntaxErrors(CodeScan.Parse(text)).Count > CodeScan.SyntaxErrors(file.Root).Count)
            {
                problems.Add(TextFormat.Format(CodeEditText.Problem_InvalidResult, file.Relative, CodeScan.SyntaxErrors(CodeScan.Parse(text)).FirstOrDefault()));
                continue;
            }

            files.Add(new FileChange(file.Path, file.Relative, file.File, text, file.File.Format));
        }

        foreach (var created in _created)
        {
            if (CodeScan.SyntaxErrors(CodeScan.Parse(created.NewText)) is [var first, ..])
            {
                problems.Add(TextFormat.Format(CodeEditText.Problem_InvalidResult, created.Relative, first));
                continue;
            }

            files.Add(created);
        }

        return (files, problems);
    }
}
