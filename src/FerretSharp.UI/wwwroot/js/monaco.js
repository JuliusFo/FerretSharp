// Bridge to the Monaco editor (vendored AMD build, ADR 0011) for the LINQ console and the SQL editor. Loads the editor
// once in the UI language (German texts for "de", Monaco's built-in English otherwise), follows light/dark, reports text
// changes to .NET, shows diagnostics as markers and asks .NET for completion items (SQL editor, LINQ console). No logic
// and no texts here: what the user reads comes from .NET.

const base = './_content/FerretSharp.UI/lib/monaco/vs';
const editors = new Map();
const completions = new Map(); // model uri → { id, dotnet } of editors that ask .NET for completion items
const providers = new Set(); // languages with a registered completion provider
const media = window.matchMedia('(prefers-color-scheme: dark)');
let loading = null;

/**
 * Loads Monaco once per page (it is global). uiLanguage is the two-letter UI language; it is fixed for the page (switching
 * the language takes a restart), so the first editor decides.
 */
function load(uiLanguage) {
  if (window.monaco) return Promise.resolve(window.monaco);
  loading ??= (async () => {
    const style = document.createElement('link');
    style.rel = 'stylesheet';
    style.href = `${base}/editor/editor.main.css`;
    document.head.appendChild(style);
    // German UI texts: a plain script setting globals (no AMD module – asking the loader for it via 'vs/nls' never
    // completes), so it goes in before the editor. English is Monaco's built-in default.
    if (uiLanguage === 'de') await script(`${base}/nls/lang/de.js`);
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

// PL/SQL colouring for the source of packages, procedures and triggers (WP-28): Monaco's own 'sql' follows T-SQL and
// knows neither ELSIF nor LOOP nor PACKAGE. Only tokens – no logic.
const plsqlKeywords = [
  'ACCESSIBLE', 'AFTER', 'AGGREGATE', 'ALL', 'ALTER', 'AND', 'ANY', 'ARRAY', 'AS', 'ASC', 'AUTHID', 'AUTONOMOUS_TRANSACTION',
  'BEFORE', 'BEGIN', 'BETWEEN', 'BODY', 'BULK', 'BY', 'CALL', 'CASE', 'CLOSE', 'COLLECT', 'COMMIT', 'COMPOUND', 'CONSTANT',
  'CONTINUE', 'CREATE', 'CROSS', 'CURRENT_USER', 'CURSOR', 'DECLARE', 'DEFAULT', 'DEFINER', 'DELETE', 'DESC', 'DETERMINISTIC',
  'DISTINCT', 'EACH', 'EDITIONABLE', 'ELSE', 'ELSIF', 'END', 'ERRORS', 'EXCEPTION', 'EXCEPTION_INIT', 'EXCEPTIONS', 'EXECUTE',
  'EXISTS', 'EXIT', 'FETCH', 'FOR', 'FORALL', 'FROM', 'FULL', 'FUNCTION', 'GOTO', 'GROUP', 'HAVING', 'IF', 'IMMEDIATE', 'IN',
  'INDEX', 'INDICES', 'INNER', 'INSERT', 'INSTEAD', 'INTERSECT', 'INTO', 'IS', 'JOIN', 'LEFT', 'LIKE', 'LIMIT', 'LOOP', 'MERGE',
  'MINUS', 'NEW', 'NOCOPY', 'NOT', 'NULL', 'OF', 'OLD', 'ON', 'OPEN', 'OR', 'ORDER', 'OTHERS', 'OUT', 'OUTER', 'OVER', 'PACKAGE',
  'PARALLEL_ENABLE', 'PARENT', 'PARTITION', 'PIPE', 'PIPELINED', 'PRAGMA', 'PRIOR', 'PROCEDURE', 'RAISE', 'RECORD', 'REF',
  'REFERENCING', 'REPLACE', 'RESULT_CACHE', 'RETURN', 'RETURNING', 'REVERSE', 'RIGHT', 'ROLLBACK', 'ROW', 'ROWTYPE', 'SAVEPOINT',
  'SELECT', 'SERIALLY_REUSABLE', 'SET', 'SQL', 'STATEMENT', 'SUBTYPE', 'TABLE', 'THEN', 'TO', 'TRIGGER', 'TYPE', 'UNION', 'UPDATE',
  'USING', 'VALUES', 'VARRAY', 'VIEW', 'WHEN', 'WHERE', 'WHILE', 'WITH', 'WRAPPED',
];
const plsqlTypes = [
  'BFILE', 'BINARY_DOUBLE', 'BINARY_FLOAT', 'BINARY_INTEGER', 'BLOB', 'BOOLEAN', 'CHAR', 'CLOB', 'DATE', 'DECIMAL', 'FLOAT',
  'INTEGER', 'INTERVAL', 'LONG', 'NATURAL', 'NCHAR', 'NCLOB', 'NUMBER', 'NVARCHAR2', 'PLS_INTEGER', 'POSITIVE', 'RAW', 'ROWID',
  'SIMPLE_INTEGER', 'SYS_REFCURSOR', 'TIMESTAMP', 'UROWID', 'VARCHAR', 'VARCHAR2', 'XMLTYPE',
];
const plsqlConstants = ['TRUE', 'FALSE', 'SQLCODE', 'SQLERRM', 'SYSDATE', 'SYSTIMESTAMP', 'USER'];

function registerPlSql(monaco) {
  if (monaco.languages.getLanguages().some(l => l.id === 'plsql')) return;
  monaco.languages.register({ id: 'plsql' });
  monaco.languages.setLanguageConfiguration('plsql', {
    comments: { lineComment: '--', blockComment: ['/*', '*/'] },
    brackets: [['(', ')']],
  });
  monaco.languages.setMonarchTokensProvider('plsql', {
    ignoreCase: true,
    keywords: plsqlKeywords,
    typeKeywords: plsqlTypes,
    constants: plsqlConstants,
    tokenizer: {
      root: [
        [/--.*$/, 'comment'],
        [/\/\*/, 'comment', '@comment'],
        [/[qQ]'\[/, 'string', '@qBracket'],
        [/[qQ]'\(/, 'string', '@qParen'],
        [/[qQ]'\{/, 'string', '@qBrace'],
        [/[qQ]'</, 'string', '@qAngle'],
        [/[nN]?'/, 'string', '@string'],
        [/"[^"]*"/, 'identifier'],
        [/:[a-zA-Z_][\w$#]*/, 'variable'],
        [/\d+(\.\d+)?([eE][+-]?\d+)?/, 'number'],
        [/[a-zA-Z_][\w$#]*/, { cases: { '@keywords': 'keyword', '@typeKeywords': 'type', '@constants': 'constant', '@default': 'identifier' } }],
        [/[;,.()]/, 'delimiter'],
        [/:=|=>|\|\||<>|!=|<=|>=|[=<>+\-*/%]/, 'operator'],
      ],
      comment: [[/\*\//, 'comment', '@pop'], [/./, 'comment']],
      string: [[/''/, 'string'], [/'/, 'string', '@pop'], [/[^']+/, 'string']],
      qBracket: [[/\]'/, 'string', '@pop'], [/./, 'string']],
      qParen: [[/\)'/, 'string', '@pop'], [/./, 'string']],
      qBrace: [[/\}'/, 'string', '@pop'], [/./, 'string']],
      qAngle: [[/>'/, 'string', '@pop'], [/./, 'string']],
    },
  });
}

media.addEventListener('change', () => { if (window.monaco) window.monaco.editor.setTheme(theme()); });

/**
 * Creates an editor in the element. options: { language, minimal (no line numbers/minimap, for the variables),
 * placeholder, readOnly (PL/SQL source: view only), readOnlyMessage (the hint when typing in it), uiLanguage (Monaco's
 * own texts, see load) }. Text changes reach .NET through OnTextChanged(id, text), debounced.
 */
export async function create(elementId, dotnet, text, options) {
  const monaco = await load(options.uiLanguage);
  if (options.language === 'plsql') registerPlSql(monaco);
  destroy(elementId);
  const element = document.getElementById(elementId);
  if (!element) return;
  const editor = monaco.editor.create(element, {
    readOnly: !!options.readOnly,
    domReadOnly: !!options.readOnly,
    readOnlyMessage: options.readOnlyMessage ? { value: options.readOnlyMessage } : undefined,
    // Monaco hides markers in read-only editors by default ('editable'); the compile errors must show.
    renderValidationDecorations: 'on',
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
  const kindOf = {
    // SQL editor
    Table: kinds.Class, View: kinds.Interface, Synonym: kinds.Reference, Column: kinds.Field, Keyword: kinds.Keyword,
    // LINQ console
    Property: kinds.Property, Field: kinds.Field, Method: kinds.Method, ExtensionMethod: kinds.Function, Class: kinds.Class,
    Struct: kinds.Struct, Interface: kinds.Interface, Enum: kinds.Enum, EnumMember: kinds.EnumMember, Variable: kinds.Variable,
    Namespace: kinds.Module, Event: kinds.Event,
  };
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
        // sortText keeps the order .NET chose (columns in schema order); Monaco still ranks by how well they match.
        suggestions: items.map((i, index) => ({
          label: { label: i.label, description: i.detail ?? undefined },
          kind: kindOf[i.kind] ?? kinds.Text,
          insertText: i.insertText,
          detail: i.detail ?? undefined,
          sortText: `${i.rank}${String(index).padStart(5, '0')}`,
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
  editor.layout(); // the view may just have become visible (PL/SQL tab switched to this part)
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
