// Bridge to the Monaco editor (vendored AMD build, ADR 0011) for the LINQ console. Loads the editor once with German
// UI texts, follows light/dark, reports text changes to .NET and shows Roslyn's diagnostics as markers. No logic here.

const base = './_content/FerretSharp.UI/lib/monaco/vs';
const editors = new Map();
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
    editor.getModel()?.dispose();
    editor.dispose();
    editors.delete(elementId);
  }
}
