# Changelog

All notable changes to this project are documented here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), versions follow [SemVer](https://semver.org/).

## [Unreleased]

## [3.6.1] - 2026-10-07

Internal restructuring (R2) without new features; a few things behave more consistently now.

### Changed
- LINQ console: on a Prod workspace, `ExecuteUpdate`/`ExecuteDelete` asks once more before writing ("Ändern auf Produktion"), like DML in the SQL editor. A running write can be cancelled with "Abbrechen".
- SQL editor: an error while scrolling to later rows of a result offers "Details" in the footer, like the LINQ console.

### Fixed
- A lost connection while writing grid changes now shows the reconnect banner instead of a write error.
- A block of rows that fails to load (e.g. after the connection was lost) no longer leaves an "Unobserved task exception" in the log.
- After clicking next to a confirmation or dialog, Esc closes it again.
- A JSON file (connections, settings, workspaces, SQL history) that could not be written completely no longer leaves a `.tmp` file behind.

## [3.6.0] - 2026-10-06

Stability release after a code review, with one undo for all writes of a workspace.

### Changed
- An error inside a tab now replaces only that tab with "In diesem Tab ist etwas schiefgelaufen" and offers "Neu laden" and "Tab schließen"; filters, SQL text and pending changes are kept. Before, any such error closed all tabs and dropped unsaved changes without asking.
- Undo (↶) takes back the last write of the workspace, whoever made it: a grid write or a statement from the SQL editor or the LINQ console. They share the workspace's transaction, and undoing an older grid write used to take every later statement along without saying so. The status bar shows how many writes are not committed yet ("2 Aktionen nicht committet", the list as tooltip, newest first), ↶ names what it takes back, and the SQL editor and LINQ console say when their statement was taken back.
- Read-only workspaces: a tab whose later rows come from a newer snapshot than its first page – because another tab of the workspace started a new query meanwhile – says so in its footer ("Stand hat sich geändert"); F5 or running again reads everything from one snapshot.
- Keyboard shortcuts do nothing while any dialog is open – also the dialogs a tab opens itself (SQL preview, plan, LOB editor). Before, Ctrl+S could write behind the LOB editor and F5 reload behind the plan dialog.

### Fixed
- Tables with a RAW primary key (GUIDs as EF Core stores them): after a reload, pending changes no longer matched their rows; editing a row again could write it twice and report a false conflict.
- Closing a workspace, disconnecting or quitting while a query ran could close the connection under the running statement; the statement is now cancelled first.
- A commit that Oracle rolls back (a deferred constraint failing at commit) no longer leaves FerretSharp believing the transaction is open; the written changes become pending again.
- A write that failed with an unexpected error is now always rolled back completely; a lost connection during that rollback shows the reconnect banner.
- A read-only (Prod) workspace stays in its read-only snapshot after a failed snapshot restart, and a lost connection there is reported as such.
- Comments, `q'[…]'` literals and quoted names no longer make a valid query look like two statements or like `FOR UPDATE`; EF Core queries with `TagWith()` comments run in the LINQ console.
- Loading the C# model or starting the LINQ console no longer hangs at "Lädt …"/"Starte …" when the build output is being rewritten or `dotnet` cannot start; the error is shown instead.
- Cancelling one request (e.g. an abandoned completion) no longer cancels other tabs loading the same table's columns.
- Workspace saving keeps changes for the next attempt on any error, and tab changes made while disconnecting are saved too.
- The SQL history is no longer overwritten when its file is locked for a moment or comes from a newer FerretSharp.
- Closing a SQL or LINQ tab while its statement runs, or a table tab while its grid starts, no longer raises an error.
- LINQ console: with the generated SQL shown, the result's footer could cover the status bar in smaller windows; the SQL now shrinks and scrolls instead.
- INSERT export: the sizes in the comments for LOBs that were not exported followed the Windows number format (`2,048 Bytes` on an English system); they are German now like all other texts.

## [3.5.0] - 2026-10-06

### Added
- Completion in the LINQ console, from your project's compiled model: after `db.` the DbSets, after `k.` in a lambda the entity's properties with their types, after `db.Kunden.` the LINQ and EF Core methods (`Where`, `Include`, `ToListAsync` …), after `Kundenart.` the enum members with their values, in `new Kunde { … }` the properties not set yet; otherwise your variables, the project's types and C# keywords. Works in the query and in the variables, also in copied code using `_context`. Ctrl+Space opens the list; nothing is suggested in strings and comments or while naming a new variable.

## [3.4.0] - 2026-10-06

### Added
- Rename SQL and LINQ tabs: double-click the tab, or click the title in the tab's toolbar. Enter or leaving the field keeps the name, Esc cancels; up to 40 characters. The name is saved with the workspace.

## [3.3.1] - 2026-10-06

### Changed
- SQL editor completion: at the end of a line, tables, views, synonyms and clause keywords (`WHERE`, `ORDER BY`, `IS NOT NULL` …) are inserted with a space after them, so you can type on right away. Columns, values and functions (`NULL`, `DESC`, `NVL` …) stay without one – a comma, parenthesis or operator usually follows – and nothing gets a space when text already follows on the line.

## [3.3.0] - 2026-10-06

### Added
- Run a whole script: Alt+X or "▶▶ Skript" in the SQL editor runs all statements – or the selected ones – one after the other. Everything is checked first (allowed statements, a writable workspace for INSERT/UPDATE/DELETE/MERGE, values for all variables): if something does not fit, nothing runs and the message names the statement. One confirmation covers the whole script on Prod or for UPDATE/DELETE without WHERE. The script stops at the first error; what it already wrote stays in the workspace's transaction. Each statement gets a result chip; a query shows the rows as they were when it ran, also if a later statement of the script changed them.

### Changed
- "Abbrechen" in the SQL editor now also stops a long-running query (Ctrl+Enter and scripts read the first page themselves).

### Fixed
- "Löschen" in the connection dialog deleted only the connection: its workspaces and SQL history stayed behind, and deleting the active connection left FerretSharp connected to it. It now works like "Löschen …" in the "⋯" menu – confirm, disconnect first (asking about uncommitted changes), remove workspaces and history.
- Results of a SQL script could close all tabs: a result grid replaced while it was still starting raised an error.
- "1 Zeilen" in the SQL editor's results.

## [3.2.0] - 2026-10-06

### Added
- SQL editor: "+ SQL" in the tab bar or Ctrl+Shift+Q opens a workspace tab for your own SQL. Separate statements with `;`, a blank line or a line with only `/`; Ctrl+Enter (or F5) runs the statement at the cursor – or the selection – in the workspace's session. The tab is saved with the workspace, script and variables included.
- Queries run everywhere (on read-only workspaces in their snapshot) and show their result in a grid with row count, time and "Plan". INSERT, UPDATE, DELETE and MERGE run only on writable workspaces, in their transaction – commit or roll back in the status bar. On Prod, and for UPDATE/DELETE without WHERE, FerretSharp asks first. DDL, PL/SQL blocks, COMMIT/ROLLBACK, ALTER SESSION and FOR UPDATE are refused with the reason.
- Bind variables: `:kundeId` gets a field below the editor with a type (text, text for CHAR columns, number, date, hex, NULL) – suggested from the column it is compared with – and values typed as in the filter bar. Values are bound, never pasted into the SQL.
- Completion (Ctrl+Space, or after `alias.`): tables, views and synonyms with their entity after FROM/JOIN/INTO/UPDATE, the columns of a table or alias in schema order with type and C# property, names quoted where Oracle needs it.
- History per connection: the last 500 statements with time, rows or error; a click adds the statement to the script with its variables (on Prod connections without values).
- Result grids (SQL editor and LINQ console) get a context menu: copy the value, copy the rows as table or as INSERT (when the query reads one table), save as CSV.
- "In SQL-Editor öffnen" in the SQL preview of a table tab: the grid's statement in a new SQL tab, its bind values as variables.

### Changed
- Free queries (SQL editor, LINQ console) show CLOB/BLOB values as preview with length, like the table grid.

## [3.1.0] - 2026-10-05

### Added
- Model cache: the C# model of a linked project is kept after it was read and reused on the next connect as long as the build output is unchanged (assemblies, deps.json, satellite assemblies, UI language) – no helper process, no `OnModelCreating` run; only the column comparison with the database remains. The model page shows "aus dem Cache · exportiert …". "Neu laden" and "Neu bauen" always read the project again.

### Fixed
- Disconnecting while a table tab was open could raise an unexpected error (the grid reacted to the model being unloaded while it was closing).

## [3.0.0] - 2026-10-05

Completes v3 – the bridge to your C#/EF Core project: model, names and enums in the grid, LINQ console, execution plans and now code generation.

### Added
- "C# ▾" at the filter bar (tables with an entity): "Als LINQ kopieren" turns the filters into `.Where(x => …)` over the entity – property names, enum members and `true`/`false` instead of database values (`x.Kundenart == Kundenart.Gewerbe && !x.Gesperrt`). The expression finds the same rows as the grid: text searches ignore case (`x.Name.ToUpper().Contains("MEIER")`), a date without time covers the whole day, "≠" includes NULL. "In LINQ-Konsole öffnen" opens a new LINQ tab with `db.Kunden.Where(…)`, ready to run.
- Grid context menu: "Als C#-Objekte kopieren" writes the selected rows as object initializers (`var kunde = new Kunde { … };`, several rows as `List<Kunde> kunden = [ new() { … } ];`, NULL values left out), "Als HasData kopieren" as `builder.HasData(…)` seed for an `IEntityTypeConfiguration`, "Als C#-Wert kopieren" a single cell (`Kundenart.Gewerbe`, `1234.50m`, `new DateTime(2026, 10, 5)`).
- What has no C# form – a column without property, a value without enum member, a value of another custom converter, a CLOB loaded only as preview – stays in the code as a comment, and the message lists it.

### Changed
- The model export carries each entity's `DbSet` name (used for `db.Kunden` and list names).

### Fixed
- The grid's context menu could reach below the window when opened low on the screen; it now moves up.

## [2.4.0] - 2026-10-05

### Added
- Execution plans: "Plan" next to "SQL" in the filter bar (the grid's query with its filters and sorting) and in the LINQ console (the captured command with the bind values from your code).
- "Geschätzt": the optimizer's plan via `EXPLAIN PLAN`, without running the query – also on Prod connections (it writes only to the session's own `PLAN_TABLE`). Bind values are unknown to it; the dialog says so.
- "Tatsächlich": the query runs again in the workspace's session with `GATHER_PLAN_STATISTICS` – the first 500 rows or, switched on, the whole result – and the dialog shows the plan Oracle used with actual rows, starts, time and buffers. Needs read access to `V$SQL`/`V$SQL_PLAN_STATISTICS_ALL` (e.g. `SELECT_CATALOG_ROLE`); without it the dialog names the grant.
- Marks: estimates off by a factor of ten or more (the usual reason for a bad plan), full table scans; steps an adaptive plan did not run are dimmed. Access and filter predicates per step. "Als Text kopieren" gives the plan in the familiar `DBMS_XPLAN` layout.

## [2.3.0] - 2026-10-05

### Added
- LINQ console (Ctrl+Shift+L or "+ LINQ" in the tab bar, with a linked C# project): a workspace tab with a C# editor (Monaco) for a query and the variables it uses. FerretSharp turns the code into exactly the SQL your project's EF Core would send – with your converters, enums and extension methods – and runs it in the workspace's session, so your own uncommitted changes are visible and locked workspaces stay read-only. The result appears in a grid, the SQL with its typed parameters beside it.
- Queries copied from code: names for the context (`_context`, `db`, `dbContext` …) and the cancellation token (`ct`, `cancellationToken`) are recognised automatically; other unknown names come back as declarations typed from how they are used (`int customerId = 0;`, `var request = new { From = DateTime.Today };`, `List<int> ids = new List<int> { };`), to adopt into the variables with one click.
- Compiler errors (in German) are marked in the editor and listed below it; a click jumps to the spot. A missing column (ORA-00904) offers the model comparison.
- `ExecuteUpdate`/`ExecuteDelete`: shown as SQL first; "Im Workspace ausführen" runs it in the workspace's transaction (commit or roll back in the status bar), only on writable workspaces.
- LINQ tabs are saved with the workspace. The console's helper process starts in the background when a LINQ tab opens and again after a new build.

### Changed
- FerretSharp ships Roslyn 4.11 and Monaco 0.57 (ADR 0011).

## [2.2.1] - 2026-10-05

### Added
- Enum members with `[Display(Name = …)]` show that text instead of the member name: "Fertigungsauftrag (0)". With `ResourceType` the text comes from the project's resources in FerretSharp's UI language (satellite assemblies such as `de\…resources.dll` included). The member name stays in the cell tooltip (`Auftragsart.ProductionOrder`), in the filter's member list and in the "Spalten" view.

### Fixed
- "+ Filter" picked an enum column as the first text column with "enthält", so its member list did not appear; it now prefers text columns without members and starts enum columns with "=".
- The member list of an enum filter sat lower than the rest of the filter row while no value was picked (since 2.2.0).

## [2.2.0] - 2026-10-05

### Added
- C# names: with a linked project, the explorer shows each table's entity and the grid header each column's property and C# type (`KundeId · int`) beside the Oracle names. New setting "C#-Namen: Aus · Daneben · Vorne" ("Vorne" puts the C# names first). The tab tooltip names the entity.
- The column search (Ctrl+F), the filter's column picker and the explorer search also find property and entity names.
- Enum and converted bool columns show their members: "Gewerbe (2)", `true`/`false`. Values without a member stay as stored and are marked (wavy underline, tooltip).
- Filtering such a column offers its members (`=`, `≠`, "in Liste" with several); the filter queries the stored values, so the SQL is the same as before. Tab titles and tooltips show the member names.
- Editing such a column: the cell editor is a list of the members (Enter or a mouse pick takes the value); FerretSharp writes the stored value.
- Relationships of the C# model without a FK constraint in the database: navigable from the cell context menu like declared foreign keys (marked "aus C#-Modell"), shown as FK badge in the grid header and the "Spalten" view, and listed in the "Constraints" view under "Beziehungen aus dem C#-Modell". A declared constraint over the same columns takes precedence.
- Sample model: `Auftrag.Bearbeiter` → `Mitarbeiter`, a relationship without constraint (`tools/sample-db/06-clr-relations.sql`).

## [2.1.2] - 2026-10-05

### Changed
- C# model: the comparison with the database reads the column names of a whole schema in one query instead of three queries per table – for a model with 425 entities over a VPN that was several minutes.
- While the model loads, the model page and the status bar show the current step ("Baue das Modell (OnModelCreating)", "Lese die Spaltennamen (…)") with its running time; afterwards the model page lists how long each step took.

## [2.1.1] - 2026-10-05

### Fixed
- C# model: an entity mapped to a view (`ToView`) was reported as "Tabelle … gibt es nicht" when a naming convention also gave it a table name. EF Core queries the view in that case (the table only serves SaveChanges), so FerretSharp now matches the view first, with the view's column names, and counts such an entity once.

## [2.1.0] - 2026-10-05

### Added
- C# model (start of v3): link a connection to the .NET project with your DbContext ("C#-Modell" in the connection dialog). FerretSharp reads the EF Core model (EF Core 8 or newer) from the existing build – with your own conventions and value converters, without running your application's start-up code and without a database connection. It runs in a helper process with your project's runtime, EF Core and Oracle provider.
- Model page (status bar "C# · N Entities"): project, build, DbContext and EF Core version; "Neu laden" and "Neu bauen" (`dotnet build`); a hint when the source code is newer than the build.
- Comparison with the database: entities without table, properties without column, columns without property, names differing only in case – the drift after changing a table before its entity. Each with a link to the table.
- "Spalten" view: entity name, and per column its property, C# type and converter; enum members with their stored values as tooltip.
- `samples/FerretSharp.SampleModel`: an EF Core 8 sample project for the sample database (naming convention in code, own converters for J/N and enum codes); `tools/sample-db/05-clr-model.sql` adds the columns it needs.

## [2.0.0] - 2026-10-05

### Added
- Unlock a workspace of a "Schreibgeschützt" connection (Prod by default) for writing: via the READ-ONLY badge, "Freischalten …" in the status bar or the workspace's "⋯" menu. On Prod you confirm by typing the connection's name. Only that workspace is unlocked – the others stay protected by Oracle – and it is never saved: closing the workspace, disconnecting or reconnecting locks it again. "Sperren" locks it manually after asking to commit or discard open changes.
- An unlocked workspace is unmistakable: striped "PROD · FREIGESCHALTET" badge, open padlock on its workspace chip, "freigeschaltet" in the status bar. Its tabs reload on unlock, since they showed the read-only snapshot.
- LOB editor for CLOB, NCLOB and BLOB cells (double-click, Enter or "Inhalt öffnen …" in the context menu): the whole value, not just the grid preview. Texts up to 10 MB can be edited directly; any LOB up to 100 MB can be saved to a file or replaced from a file (text files as UTF-8, UTF-16 or Windows-1252), or set to NULL. BLOBs show a hex view and images (PNG, JPEG, GIF, BMP, WebP) as a preview. On locked workspaces the editor opens read-only – handy to read a long CLOB or save a BLOB.
- LOB changes are pending changes like cell edits (write, commit, undo, rollback); the concurrency check compares the whole value as it was when the editor opened.

### Changed
- Long values in SQL previews, error dialogs and logs are shortened (texts after 500 characters, binary values after 64 bytes, with their full length).

### Fixed
- Double-clicking a cell of a new row (pinned at the top) did not start editing in the app (since 1.9.0).
- The status bar no longer wraps into two cramped lines in narrower windows: entries stay on one line, connection, workspace, server version and schema are shortened with "…" (full text as tooltip), and server version and schema give way entirely when space runs out.
- The window cannot be made smaller than 960 × 560.

## [1.9.0] - 2026-10-05

### Added
- Editing on connections without "Schreibgeschützt" (badge BEARBEITBAR): double-click a cell or start typing, Enter checks the value (length, number, date, hex) and keeps the editor open with a message if it is invalid. "+ Zeile" adds a row at the top, Del (or the context menu) marks rows for deletion, "Ausstehende Änderungen verwerfen" reverts them.
- Changes are pending (yellow) until "Schreiben" (Ctrl+S) writes them into the workspace's transaction (blue): rows are then locked and still invisible to other workspaces and users until "Commit" (Ctrl+Shift+Enter, writes pending changes first; on Prod with confirmation). "Rollback" discards everything, "↶" takes back the last write, "SQL" shows the statements of the pending changes.
- Each write is all or nothing. Rows locked by another session are waited for up to 3 seconds (configurable in the settings), then a dialog explains the lock and offers to retry; the locking session is named if the user may read V$SESSION. If someone changed a value you edited in the meantime, a dialog shows load time, database and your value and lets you overwrite or drop your change. Rejected writes (constraint violations etc.) explain the Oracle error and leave everything pending.
- Status bar with the transaction age (warning after 10 minutes), pending/written counts and the buttons; a dot on tabs with changes.
- No silent loss of uncommitted work: closing a tab with pending changes asks; closing a workspace, disconnecting, switching or deleting the connection and quitting FerretSharp offer to commit or discard first.
- Not editable (tooltip explains why): views, tables without row key, virtual and identity columns, primary key values of existing rows, LOB/LONG and other special types.

### Fixed
- The SQL preview now closes with Escape right away.

## [1.8.0] - 2026-10-04

### Added
- Connections marked "Schreibgeschützt" (Prod by default) are now protected by Oracle itself: the workspace sessions run in a `SET TRANSACTION READ ONLY` transaction, so the database rejects any change (ORA-01456) – not only FerretSharp's own checks.
- On such connections the tab footer shows the data snapshot ("Stand 14:02:13"). Every new query (opening a tab, filter, sort, F5) gets current data; further pages of the same query stay in the same snapshot, so paging neither repeats nor skips rows while others change the table.
- If a snapshot can no longer read a table (changed or newly filled meanwhile, or the undo is too old), FerretSharp starts a new snapshot and repeats the query instead of showing an error.
- Status bar: "schreibgeschützt (Oracle)" for such connections; the READ-ONLY badge explains which protection applies.
- Transaction groundwork for editing (v2): transactions with savepoints, commit and rollback, and a single internal write path that accepts only INSERT/UPDATE/DELETE inside a transaction (never DDL). Nothing uses it yet.

## [1.7.0] - 2026-10-03

### Added
- Detail views next to "Daten": Spalten | Constraints | Indizes | Abhängigkeiten | DDL, below a header with the table comment, status, creation and last DDL time, row count of the optimizer statistics and tablespace. Views stay loaded per tab; F5 reloads them.
  - Spalten: column comments, VIRTUAL and DEFAULT ON NULL.
  - Constraints: primary/unique/foreign keys and check constraints with condition, ON DELETE rule and state (disabled, not validated, deferrable); the system NOT NULL checks stay with the columns.
  - Indizes: columns, function-based expressions, DESC, uniqueness, state (UNUSABLE, invisible); a hint lists foreign keys without a supporting index.
  - Abhängigkeiten: what the object uses and what uses it (views, packages, triggers, synonyms), with invalid ones marked.
  - DDL from DBMS_METADATA, with copy button (shown only, never executed).
- Invalid views and materialized views are red in the explorer; the tooltip explains what that means for each kind.
- Column comments as tooltips in the grid header and the column lists.
- `tools/sample-db/04-object-details.sql`: comments, check constraints, indexes, an invalid view and statistics for the sample database.

### Changed
- "Struktur" is now "Spalten"; keys and foreign keys moved to "Constraints".

## [1.6.0] - 2026-10-03

### Added
- "Zurück" after FK jumps: a tab opened by a jump shows "← <table>" in its toolbar; that button or Alt+Left activates the tab the jump came from (or the nearest open one further back if it was closed), Alt+Right goes forward again. The origin is saved with the workspace.
- Ctrl+F in the structure view filters the column table (same matching as the column search); Escape clears the filter.
- Keep-alive: sessions idle for a minute are pinged every 2 minutes, so a lost connection shows the reconnect banner at once instead of on the next click, and firewalls do not drop idle connections. Can be switched off in the settings ("Verbindung im Leerlauf prüfen").

### Changed
- When a table is open in several tabs (e.g. after FK jumps), the tab titles show a short form of their filters, e.g. "KUNDEN KUNDE_ID = 4711".

## [1.5.0] - 2026-10-03

### Added
- Ctrl+F (or "Spalte" in the tab toolbar) finds a column by name and jumps to it: the grid scrolls there, highlights the column and focuses its cell in the first visible row. The browser's own search could not find columns the grid had not rendered.

### Changed
- The column of a filter row is chosen in a search field instead of a drop-down: typing filters the columns (several words, underscores optional, e.g. "liefer ort" or "lieferort"), arrow keys choose, Enter or Tab takes the column, Escape keeps the previous one. "+ Filter" puts the cursor there right away.

### Fixed
- After switching a filter row to a column of another type, the operator list could show a different operator than the one actually used (e.g. "enthält" while filtering with "=").

## [1.4.0] - 2026-10-02

### Added
- Pin columns to the left of the grid: right-click a column header ("Spalte links anheften" / "Spalte lösen"), use the same entry in the cell menu, or drag a column into the pinned area. Pinned columns follow the primary key, which always stays pinned at the very left, and are saved per tab with the workspace.

### Changed
- The edge of the pinned area is drawn as a stronger line.

## [1.3.0] - 2026-10-02

### Added
- Ctrl+C in the grid copies the value of the focused cell, or the selected rows as a table (tab-separated with header, pastes into Excel) when several rows are selected. Text marked inside a cell is copied as usual.

### Changed
- "Wert kopieren" copies the full value instead of the shortened display text (long texts were cut after 1,000 characters and line breaks became ⏎; numbers no longer contain thousands separators).
- Shift+click in the grid no longer marks text across the cells.

## [1.2.0] - 2026-10-02

### Added
- App icon: "FS" monogram (F dark, S blue) on a light rounded tile, in the exe, title bar and taskbar. Generated by `tools/icon/New-AppIcon.ps1`.
- "⋯" menu on workspace chips with Umbenennen, Schließen and Neuer Workspace (like the menu of the connections). Double click, × and middle click still work.

## [1.1.1] - 2026-10-02

### Fixed
- Double click did nothing with a real mouse (renaming a workspace, connecting from the connections page): the WPF host passes the second mouse-down to the WebView twice, so the browser never raises a double-click event. Double clicks are now detected from the click count.
- Renaming a workspace selects the whole name, so typing replaces it.

## [1.1.0] - 2026-10-02

### Added
- Settings page (icon in the top bar) with the color scheme: System, Light or Dark – switches immediately, including the title bar, and is saved in `settings.json`. `--theme=dark|light` still overrides it for a session.

## [1.0.1] - 2026-10-02

### Fixed
- Primary and red (delete) buttons lost their background on hover, leaving the text unreadable (light text on the light hover color, dark text on the dark one).
- Red buttons in dark mode had poor contrast (white on light red, 2.8:1); they now use dark text (6.8:1).

## [1.0.0] - 2026-10-02

First release: read-only Oracle browser (v1).

### Added
- Export of selected rows (Ctrl/Shift multi-selection): copy as tab-separated table (pastes into Excel) or as INSERT statements, save as CSV (semicolon, UTF-8 with BOM) or as SQL script. INSERT scripts use exact Oracle literals and are never executed; LOB values that were only loaded as preview are left out with a warning.
- Error dialog for database errors with ORA code, message, the failing statement and its bind variables; bind values of Prod connections are masked there and in the log.
- Connection loss (killed session, idle timeout, network) is recognized: banner and status bar offer "Neu verbinden", which restores all workspaces, tabs and filters.
- Notices (export done, warnings) as toasts.
- Read-only guard: sessions refuse every statement that is not a plain query (no DML, DDL, PL/SQL or `FOR UPDATE`), backed by architecture tests.
- Solution scaffold (`FerretSharp.slnx`, .NET 10, central package management).
- Blazor Hybrid shell: thin WPF host with `BlazorWebView`, platform-neutral Razor UI library (`FerretSharp.UI`) with design tokens for light/dark, native title bar following the theme, `--theme=dark|light` override.
- Generic host with DI and Serilog file logging (`%APPDATA%\FerretSharp\logs`), global exception handlers.
- Unit test project and Oracle integration test project (Testcontainers, skipped without Docker).
- Connections: add/edit/delete Oracle connection profiles (host/port with service name or SID, or TNS alias with `TNS_ADMIN`), environment kind with color coding, read-only default for Prod, connection test showing duration, server version and ORA error codes.
- Connection groups (e.g. project/customer), connection switcher popover with search, collapsible groups and keyboard navigation (`Ctrl+Shift+O`), and a connections overview page with edit, duplicate (reuses the original's password) and delete with confirmation.
- Passwords are stored in the Windows Credential Manager, never in `connections.json`.
- Read-only `OracleSession` (pooling off, `V$SESSION` module/action/client info, serialized and cancellable queries).
- `--data-dir=<path>` redirects connections and logs to another folder.
- Connect/disconnect with progress, cancellation and an error view showing the ORA code; recently used connections in the switcher and on the overview page; red frame while a Prod connection is active.
- Schema explorer: tables, views and materialized views of the connection's schema with search and A–Z letter index; object list and foreign keys load on connect, column details lazily.
- Table structure view: columns with types, defaults, identity and PK/UK/FK markers, keys, row key, incoming and outgoing foreign keys with navigation.
- Data grid (AG Grid Community 34.3.1, vendored): tables open as tabs, rows load in blocks of 500 while scrolling, server-side sorting (multi-sort with Shift), German formatting, explicit NULL, CLOB preview, exact NUMBER(38), placeholders for unsupported types.
- TablePlus-style filter bar: per-column operators, German/ISO value parsing with validation, case-insensitive contains with literal `%`/`_`, whole-day date semantics, IN lists, enable/disable per row, `Ctrl+Enter` to apply, SQL preview with bind variables.
- Row count on demand (cancellable), `F5` reloads the grid.
- View and materialized view definitions (highlighted SQL) in the structure view.
- Synonyms: tables, views and materialized views of other schemas reached through private or public synonyms appear in the explorer (tag "SYN") and open like own objects; foreign keys of those schemas are loaded too.
- Workspaces per connection: chips in the top bar to create, rename (double-click), close (also middle-click), reopen and delete workspaces. Each open workspace queries on its own Oracle session (`V$SESSION` ACTION = workspace name); tabs, filters (including unapplied edits), sort order and scroll position are saved automatically and restored after a restart.
- Foreign key navigation: right-click a cell to jump to the referenced row or to the referencing rows (with counts, cancelled after 5 s), including composite keys and RAW(16)/GUID keys; every jump opens a new filtered tab. "Copy value" in the same menu.
- RAW columns can be filtered by hex value (`=`, `≠`, `in`).

### Fixed
- Sorting followed the Windows locale (`NLS_SORT=GERMAN`); sessions now sort and compare binary.
- Middle-click on a tab did nothing; it now closes the tab.
- A foreign key jump without referencing rows covered the whole context menu and swallowed clicks.
- Non-ASCII characters in a session's ACTION or CLIENT_INFO (e.g. a workspace named "Prüfung") made ODP.NET lose the connection (ORA-12537); they are now transliterated to ASCII.
