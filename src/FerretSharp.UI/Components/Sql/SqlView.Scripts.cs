using FerretSharp.Core.Connections;
using FerretSharp.Core.Query;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Settings;
using FerretSharp.Core.Scripts;
using FerretSharp.Core.Workspaces;
using FerretSharp.UI.Resources;
using FerretSharp.UI.State;
using Microsoft.AspNetCore.Components;

namespace FerretSharp.UI.Components.Sql;

/// <summary>What the bar above a script tab says.</summary>
public enum ScriptNoticeKind { Info, Error, ChangedOnDisk, Missing }

/// <summary>A message about the tab's script file; <see cref="FileHash"/> is the file on disk for <see cref="ScriptNoticeKind.ChangedOnDisk"/>.</summary>
public sealed record ScriptNotice(ScriptNoticeKind Kind, string Text, string? FileHash = null);

/// <summary>A question before a script file is replaced.</summary>
public enum ScriptConfirm { ReplaceExisting, OverwriteChanged }

/// <summary>
/// WP-77: SQL scripts in the SQL editor – save the tab, the selection or a statement of the history as a script, insert a
/// script at the cursor, and keep a tab linked to its file: saved only by "Save", reloaded quietly when the file changed on
/// disk and the tab has no changes of its own, otherwise asked.
/// </summary>
public partial class SqlView
{
    [Inject]
    private ScriptLibrary Library { get; set; } = null!;

    [Inject]
    private IFileReveal FileReveal { get; set; } = null!;

    private bool _scriptsOpen;
    private ScriptNotice? _scriptNotice;
    private ScriptSaveDraft? _save;
    private string? _saveError;
    private bool _saving;
    private ScriptConfirm? _scriptConfirm;

    /// <summary>The file as last looked at (time, length): the library reports every folder's change, the hash is read only when this one changed.</summary>
    private (DateTime Written, long Length)? _checkedStamp;

    /// <summary>A change on disk the user chose to keep their own text over ("Keep"): not asked again for the same file.</summary>
    private string? _keptHash;

    private string SaveTitle => Tab.Script is { } link
        ? TextFormat.Format(ScriptViewText.SqlView_SaveTitle, link.Path) + Shortcuts.Hint(ShortcutAction.SaveScript)
        : ScriptViewText.SqlView_SaveNewTitle + Shortcuts.Hint(ShortcutAction.SaveScript);

    private IReadOnlyList<MoreMenuItem> ScriptMenu =>
    [
        Tab.Script is null
            ? new MoreMenuItem(ScriptViewText.Menu_SaveWindow, SaveWindowAsAsync) { Strong = true }
            : new MoreMenuItem(ScriptViewText.Menu_SaveAs, SaveWindowAsAsync),
        new MoreMenuItem(ScriptViewText.Menu_SaveSelection, SaveSelectionAsync) { Title = ScriptViewText.Menu_SaveSelectionTitle },
        new MoreMenuItem(ScriptViewText.Menu_Reveal, RevealScriptFile, Tab.Script is not null && RootOf(Tab.Script) is not null) { SeparatorBefore = true },
    ];

    private string ScriptLinkTitle(ScriptLink link) => RootOf(link) is { } root
        ? TextFormat.Format(ScriptViewText.SqlView_LinkTitle, ScriptTree.RootLabel(root), link.Path)
        : TextFormat.Format(ScriptViewText.SqlView_LinkNoRoot, link.Path);

    /// <summary>The folder a link names among those of this connection; null if the connection has no such folder (any more).</summary>
    private ScriptRoot? RootOf(ScriptLink link) => ScriptRoots.Find(Library.RootsFor(Active.Profile), link.Root, link.Group);

    private IReadOnlyList<string> FoldersOf(ScriptRoot root) =>
        Library.For(Active.Profile).FirstOrDefault(s => s.Root == root)?.Folders ?? [];

    private void ToggleScripts()
    {
        _scriptsOpen = !_scriptsOpen;
        _historyOpen &= !_scriptsOpen; // one panel beside the editor at a time
    }

    /// <summary>The editor's text now (it reports changes only after a pause).</summary>
    private async Task<string> CurrentTextAsync()
    {
        if (_editor is not null && await _editor.GetTextAsync() is { } text && text != Tab.Text)
        {
            Tab.Text = text;
            await UpdateVariablesAsync(text);
        }

        return Tab.Text;
    }

    // ---- save ----

    /// <summary>"Save": into the linked file, else as a new script (the dialog).</summary>
    private async Task SaveScriptAsync()
    {
        var text = await CurrentTextAsync();
        if (Tab.Script is not { } link)
        {
            OpenSaveDialog(new ScriptSaveRequest(ScriptSaveSource.Window, text, Tab.Variables, SuggestedName()));
            return;
        }

        if (RootOf(link) is not { } root)
        {
            // The group's folder is no longer set, or the connection is in another group: save elsewhere.
            _scriptNotice = new(ScriptNoticeKind.Error, TextFormat.Format(ScriptViewText.Notice_NoRoot, link.Path));
            OpenSaveDialog(new ScriptSaveRequest(ScriptSaveSource.Window, text, Tab.Variables, link.Name, link));
            return;
        }

        await WriteLinkedAsync(root, link, text, ScriptWriteMode.Update);
    }

    private async Task SaveWindowAsAsync()
    {
        var text = await CurrentTextAsync();
        OpenSaveDialog(new ScriptSaveRequest(ScriptSaveSource.Window, text, Tab.Variables, Tab.Script?.Name ?? SuggestedName(), Tab.Script));
    }

    /// <summary>The selection – or, without one, the statement at the cursor.</summary>
    private async Task SaveSelectionAsync()
    {
        if (_editor is null || await _editor.GetRunContextAsync() is not { } context)
        {
            return;
        }

        var text = context.End > context.Start
            ? context.Text[context.Start..context.End]
            : SqlScript.StatementAt(context.Text, context.Start, context.End)?.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            _scriptNotice = new(ScriptNoticeKind.Info, ScriptViewText.Notice_NothingSelected);
            return;
        }

        OpenSaveDialog(new ScriptSaveRequest(ScriptSaveSource.Selection, text.Trim(), Tab.Variables, ""));
    }

    private void SaveHistoryEntry(SqlHistoryEntry entry) =>
        OpenSaveDialog(new ScriptSaveRequest(ScriptSaveSource.History, entry.Sql, entry.Variables, ""));

    /// <summary>The tab's title as the name of a new script, unless it is one FerretSharp made up ("SQL 3").</summary>
    private string SuggestedName() =>
        Tab.Title.StartsWith("SQL ", StringComparison.Ordinal) && int.TryParse(Tab.Title[4..], System.Globalization.CultureInfo.InvariantCulture, out _)
            ? ""
            : Tab.Title;

    private void OpenSaveDialog(ScriptSaveRequest request)
    {
        (_save, _saveError, _saving) = (new ScriptSaveDraft(request, Library.RootsFor(Active.Profile)), null, false);
    }

    private void CloseSaveDialog() => (_save, _saveError, _scriptConfirm) = (null, null, null);

    /// <summary>Writes the dialog's script; a file of that name is replaced only after asking.</summary>
    private async Task SaveDraftAsync(bool replace)
    {
        if (_save is not { CanSave: true } draft || _saving)
        {
            return;
        }

        (_saving, _saveError) = (true, null);
        try
        {
            var (root, path, header, text) = (draft.Root, draft.Path, draft.Header, draft.Request.Text);
            ScriptWrite result;
            try
            {
                result = await ScriptFiles.WriteAsync(root.Path, path, header, text, replace ? ScriptWriteMode.Replace : ScriptWriteMode.New, null, CancellationToken.None);
            }
            catch (RefusedException ex)
            {
                _saveError = ex.Message;
                return;
            }

            if (result.Outcome == ScriptWriteOutcome.Exists)
            {
                _scriptConfirm = ScriptConfirm.ReplaceExisting;
                return;
            }

            if (draft.Request.Source == ScriptSaveSource.Window)
            {
                // The tab now shows the file: named after it, saved only by "Save" from here on.
                Tab.Script = ScriptLink.To(root, path, header, text, result.FileHash!);
                Tab.Title = ScriptPaths.NameOf(path);
                (_checkedStamp, _keptHash) = (null, null);
                Shell.MarkDirty();
                _scriptNotice = null;
            }
            else
            {
                _scriptNotice = new(ScriptNoticeKind.Info, TextFormat.Format(ScriptViewText.Notice_Saved, ScriptPaths.NameOf(path), ScriptTree.RootLabel(root)));
            }

            _save = null;
            await Library.RefreshAsync();
        }
        finally
        {
            _saving = false;
        }
    }

    /// <summary>Writes the tab into its file; a file changed on disk since it was read is replaced only after asking.</summary>
    private async Task WriteLinkedAsync(ScriptRoot root, ScriptLink link, string text, ScriptWriteMode mode)
    {
        var header = link.HeaderFor(text, Tab.Variables);
        ScriptWrite result;
        try
        {
            result = await ScriptFiles.WriteAsync(root.Path, link.Path, header, text, mode, link.FileHash, CancellationToken.None);
        }
        catch (RefusedException ex)
        {
            _scriptNotice = new(ScriptNoticeKind.Error, ex.Message);
            return;
        }

        if (result.Outcome != ScriptWriteOutcome.Written)
        {
            _scriptConfirm = ScriptConfirm.OverwriteChanged;
            return;
        }

        Tab.Script = ScriptLink.To(root, link.Path, header, text, result.FileHash!);
        (_checkedStamp, _keptHash, _scriptNotice) = (null, null, null);
        Shell.MarkDirty();
        await Library.RefreshAsync();
    }

    private string ScriptConfirmText(ScriptConfirm question) => question == ScriptConfirm.ReplaceExisting && _save is { } draft
        ? TextFormat.Format(ScriptViewText.Confirm_ReplaceText, draft.Path, ScriptTree.RootLabel(draft.Root))
        : TextFormat.Format(ScriptViewText.Confirm_ChangedText, Tab.Script?.Path);

    private async Task ConfirmScriptAsync()
    {
        var question = _scriptConfirm;
        _scriptConfirm = null;
        if (question == ScriptConfirm.ReplaceExisting)
        {
            await SaveDraftAsync(replace: true);
        }
        else if (Tab.Script is { } link && RootOf(link) is { } root)
        {
            await WriteLinkedAsync(root, link, await CurrentTextAsync(), ScriptWriteMode.Replace);
        }
    }

    // ---- insert ----

    /// <summary>
    /// Inserts a script at the cursor or in place of the selection, the statement before it ended (decision of the user);
    /// its parameters join the variables, values already typed stay. The tab is not linked to the file.
    /// </summary>
    private async Task InsertScriptAsync(ScriptEntry script)
    {
        if (_editor is null)
        {
            return;
        }

        ScriptDocument document;
        try
        {
            document = await ScriptFiles.ReadAsync(script.Root.Path, script.Path, CancellationToken.None);
        }
        catch (RefusedException ex)
        {
            _scriptNotice = new(ScriptNoticeKind.Error, ex.Message);
            return;
        }

        var context = await _editor.GetRunContextAsync();
        var current = context?.Text ?? Tab.Text;
        var (text, start) = SqlScript.Insert(current, context?.Start ?? current.Length, context?.End ?? current.Length, document.Script.Body);
        Tab.Text = text;
        Tab.Variables = ScriptHeaders.AddTo(Tab.Variables, document.Script.Header.Parameters);
        await _editor.SetTextAsync(text);
        await UpdateVariablesAsync(text);
        Shell.MarkDirty();
        await _editor.RevealOffsetAsync(start);
    }

    // ---- the linked file on disk ----

    private void OnLibraryChanged() => Fire(CheckScriptFileAsync);

    /// <summary>
    /// Looks whether the linked file changed on disk (a <c>git pull</c>): without changes of the tab's own it is reloaded
    /// quietly, otherwise the bar asks "Reload / Keep" (decision of the user, 2026-11-08). A file that is gone is said so;
    /// "Save" creates it again.
    /// </summary>
    private async Task CheckScriptFileAsync()
    {
        if (Tab.Script is not { } link || RootOf(link) is not { } root)
        {
            return;
        }

        if (Library.Find(root, link.Path) is not { } entry)
        {
            if (Library.For(Active.Profile).FirstOrDefault(s => s.Root == root) is { Loaded: true, Error: null })
            {
                _scriptNotice = new(ScriptNoticeKind.Missing, TextFormat.Format(ScriptViewText.Notice_Missing, link.Path));
                _checkedStamp = null;
                StateHasChanged();
            }

            return;
        }

        if (_checkedStamp == (entry.Written, entry.Length))
        {
            return;
        }

        _checkedStamp = (entry.Written, entry.Length);
        string? hash;
        try
        {
            hash = await ScriptFiles.FileHashAsync(root.Path, link.Path, CancellationToken.None);
        }
        catch (RefusedException ex)
        {
            _scriptNotice = new(ScriptNoticeKind.Error, ex.Message);
            StateHasChanged();
            return;
        }

        if (Tab.Script != link)
        {
            return; // saved or relinked meanwhile
        }

        if (hash is null || hash == link.FileHash || hash == _keptHash)
        {
            if (_scriptNotice?.Kind is ScriptNoticeKind.Missing or ScriptNoticeKind.ChangedOnDisk && hash == link.FileHash)
            {
                _scriptNotice = null;
                StateHasChanged();
            }

            return;
        }

        if (!Tab.ScriptChanged)
        {
            await ReloadAsync(root, link);
        }
        else
        {
            _scriptNotice = new(ScriptNoticeKind.ChangedOnDisk, TextFormat.Format(ScriptViewText.Notice_ChangedOnDisk, link.Path), hash);
        }

        StateHasChanged();
    }

    private async Task ReloadFromDiskAsync()
    {
        if (Tab.Script is { } link && RootOf(link) is { } root)
        {
            await ReloadAsync(root, link);
        }
    }

    /// <summary>"Keep": the tab's text stays; "Save" still asks before replacing the changed file.</summary>
    private void KeepOwnText()
    {
        _keptHash = _scriptNotice?.FileHash;
        _scriptNotice = null;
    }

    /// <summary>The file's text and parameter types into the tab; values typed stay.</summary>
    private async Task ReloadAsync(ScriptRoot root, ScriptLink link)
    {
        ScriptDocument document;
        try
        {
            document = await ScriptFiles.ReadAsync(root.Path, link.Path, CancellationToken.None);
        }
        catch (RefusedException ex)
        {
            _scriptNotice = new(ScriptNoticeKind.Error, ex.Message);
            return;
        }

        var script = document.Script;
        Tab.Text = script.Body;
        Tab.Variables = ScriptHeaders.Apply(Tab.Variables, script.Header.Parameters);
        Tab.Script = ScriptLink.To(root, link.Path, script.Header, script.Body, document.FileHash);
        (_keptHash, _scriptNotice) = (null, null);
        if (_editor is not null)
        {
            await _editor.SetTextAsync(script.Body);
        }

        await UpdateVariablesAsync(script.Body);
        Shell.MarkDirty();
    }

    private void RevealScriptFile()
    {
        if (Tab.Script is { } link && RootOf(link) is { } root)
        {
            try
            {
                FileReveal.Reveal(ScriptPaths.Resolve(root.Path, link.Path));
            }
            catch (RefusedException ex)
            {
                _scriptNotice = new(ScriptNoticeKind.Error, ex.Message);
            }
        }
    }
}
