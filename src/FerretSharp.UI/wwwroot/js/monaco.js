// Bridge to the Monaco editor (vendored AMD build, ADR 0011) for the LINQ console and the SQL editor. Loads the editor
// once with German UI texts, follows light/dark, reports text changes to .NET, shows diagnostics as markers and asks .NET
// for completion items (SQL editor). No logic here.

const base = './_content/FerretSharp.UI/lib/monaco/vs';
const editors = new Map();
const completions = new Map(); // model uri → { id, dotnet } of editors that ask .NET for completion items
const providers = new Set(); // languages with a registered completion provider
const media = window.matchMedia('(prefers-color-scheme: dark)');
let loading = null;

function load() {
  if (window.monaco) return Promise.resolve(window.monaco);
  loading ??= (async () => {
    const style = document.createElement('link');
    style.rel = 'stylesheet';
    style.href = `${base}/editor/editor.main.css`;
    document.head.appendChild(style);
    // German UI texts: a plain script setting globals (no AMD module – asking the loader for it via 'vs/nls' never
    // completes), so it goes in before the editor.
    await script(`${base}/nls/lang/de.js`);
    await script(`${base}/loader.js`);
    window.require.config({ paths: { vs: base } });
    return await new Promise((resolve, reject) => window.require(['vs/editor/editor.main'], () => resolve(window.monaco), reject));
  })();
  return loading;
}

function script(src) {
  return new Promise((resolve, reject) => {
    const element = document.createElement('script');
    element.src = src;
    element.onload = resolve;
    element.onerror = () => reject(new Error(`${src} could not be loaded.`));
    document.head.appendChild(element);
  });
}

function theme() { return media.matches ? 'vs-dark' : 'vs'; }

media.addEventListener('change', () => { if (window.monaco) window.monaco.editor.setTheme(theme()); });

/**
 * Creates an editor in the element. options: { language, minimal (no line numbers/minimap, for the variables),
 * placeholder }. Text changes reach .NET through OnTextChanged(id, text), debounced.
 */
export async function create(elementId, dotnet, text, options) {
  const monaco = await load();
  destroy(elementId);
  const element = document.getElementById(elementId);
  if (!element) return;
  const editor = monaco.editor.create(element, {
    value: text ?? '',
    language: options.language ?? 'csharp',
    theme: theme(),
    automaticLayout: true,
    minimap: { enabled: false },
    lineNumbers: options.minimal ? 'off' : 'on',
    glyphMargin: false,
    folding: !options.minimal,
    lineDecorationsWidth: options.minimal ? 8 : 10,
    scrollBeyondLastLine: false,
    renderLineHighlight: options.minimal ? 'none' : 'line',
    fontFamily: '"Cascadia Mono", "Cascadia Code", Consolas, monospace',
    fontSize: 13,
    tabSize: 4,
    wordWrap: 'off',
    fixedOverflowWidgets: true,
    placeholder: options.placeholder ?? undefined,
  });
  let timer = null;
  editor.onDidChangeModelContent(() => {
    clearTimeout(timer);
    timer = setTimeout(() => dotnet.invokeMethodAsync('OnTextChanged', elementId, editor.getValue()).catch(() => {}), 250);
  });
  editors.set(elementId, editor);
  if (options.completion) {
    completions.set(editor.getModel().uri.toString(), { id: elementId, dotnet });
    registerProvider(monaco, options.language ?? 'csharp');
  }
}

/** One provider per language; it asks the .NET side of the editor whose model is being edited. */
function registerProvider(monaco, language) {
  if (providers.has(language)) return;
  providers.add(language);
  const kinds = monaco.languages.CompletionItemKind;
  const kindOf = { Table: kinds.Class, View: kinds.Interface, Synonym: kinds.Reference, Column: kinds.Field, Keyword: kinds.Keyword };
  monaco.languages.registerCompletionItemProvider(language, {
    triggerCharacters: ['.'],
    provideCompletionItems: async (model, position) => {
      const target = completions.get(model.uri.toString());
      if (!target) return { suggestions: [] };
      const word = model.getWordUntilPosition(position);
      const range = { startLineNumber: position.lineNumber, endLineNumber: position.lineNumber, startColumn: word.startColumn, endColumn: word.endColumn };
      let items = [];
      try {
        items = await target.dotnet.invokeMethodAsync('OnComplete', target.id, model.getValue(), model.getOffsetAt(position));
      } catch {
        return { suggestions: [] };
      }
      return {
        suggestions: items.map(i => ({
          label: { label: i.label, description: i.detail ?? undefined },
          kind: kindOf[i.kind] ?? kinds.Text,
          insertText: i.insertText,
          detail: i.detail ?? undefined,
          sortText: `${i.rank}${i.label}`,
          range,
        })),
      };
    },
  });
}

/** Text, selection and cursor as offsets: what Ctrl+Enter runs is decided in .NET. */
export function getRunContext(elementId) {
  const editor = editors.get(elementId);
  if (!editor) return null;
  const model = editor.getModel();
  const selection = editor.getSelection();
  return {
    text: model.getValue(),
    start: model.getOffsetAt(selection.getStartPosition()),
    end: model.getOffsetAt(selection.getEndPosition()),
  };
}

/** Briefly highlights the statement that runs. */
export function flash(elementId, start, length) {
  const editor = editors.get(elementId);
  if (!editor || !window.monaco) return;
  const model = editor.getModel();
  const range = window.monaco.Range.fromPositions(model.getPositionAt(start), model.getPositionAt(start + length));
  const decorations = editor.createDecorationsCollection([{ range, options: { className: 'fs-ran-statement', isWholeLine: false } }]);
  setTimeout(() => decorations.clear(), 900);
}

/** Markers by text offsets: [{ start, length, severity: 'error'|'warning', message }]. */
export function setOffsetMarkers(elementId, markers) {
  const editor = editors.get(elementId);
  if (!editor || !window.monaco) return;
  const model = editor.getModel();
  const severity = window.monaco.MarkerSeverity;
  window.monaco.editor.setModelMarkers(model, 'ferretsharp', (markers ?? []).map(m => {
    const from = model.getPositionAt(m.start);
    const to = model.getPositionAt(m.start + Math.max(1, m.length));
    return {
      startLineNumber: from.lineNumber, startColumn: from.column, endLineNumber: to.lineNumber, endColumn: to.column,
      severity: m.severity === 'error' ? severity.Error : severity.Warning, message: m.message,
    };
  }));
}

/** Puts the cursor at a text offset, shows it and focuses the editor. */
export function revealOffset(elementId, offset) {
  const editor = editors.get(elementId);
  if (!editor) return;
  const position = editor.getModel().getPositionAt(offset);
  editor.setPosition(position);
  editor.revealPositionInCenterIfOutsideViewport(position);
  editor.focus();
}

/** Replaces the whole text (e.g. after adding suggested declarations), keeping undo. */
export function setText(elementId, text) {
  const editor = editors.get(elementId);
  if (!editor) return;
  const model = editor.getModel();
  editor.executeEdits('ferretsharp', [{ range: model.getFullModelRange(), text: text ?? '' }]);
  editor.pushUndoStop();
}

export function getText(elementId) {
  return editors.get(elementId)?.getValue() ?? null;
}

/** Diagnostics: [{ line, column, endLine, endColumn, severity: 'error'|'warning', message }] (1-based). */
export function setMarkers(elementId, markers) {
  const editor = editors.get(elementId);
  if (!editor || !window.monaco) return;
  const severity = window.monaco.MarkerSeverity;
  window.monaco.editor.setModelMarkers(editor.getModel(), 'ferretsharp', (markers ?? []).map(m => ({
    startLineNumber: m.line, startColumn: m.column, endLineNumber: m.endLine, endColumn: Math.max(m.endColumn, m.column + 1),
    severity: m.severity === 'error' ? severity.Error : severity.Warning, message: m.message,
  })));
}

/** Puts the cursor at a position and focuses the editor (click on a diagnostic). */
export function reveal(elementId, line, column) {
  const editor = editors.get(elementId);
  if (!editor) return;
  editor.setPosition({ lineNumber: line, column });
  editor.revealLineInCenterIfOutsideViewport(line);
  editor.focus();
}

export function focus(elementId) {
  editors.get(elementId)?.focus();
}

/** Ctrl+F is a global shortcut (column search); in the console it opens the editor's find widget. */
export function find(elementId) {
  const editor = editors.get(elementId);
  if (!editor) return;
  editor.focus();
  editor.getAction('actions.find')?.run();
}

export function destroy(elementId) {
  const editor = editors.get(elementId);
  if (editor) {
    const model = editor.getModel();
    if (model) completions.delete(model.uri.toString());
    model?.dispose();
    editor.dispose();
    editors.delete(elementId);
  }
}
