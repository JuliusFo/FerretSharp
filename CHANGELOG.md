# Changelog

All notable changes to this project are documented here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), versions follow [SemVer](https://semver.org/).

## [Unreleased]

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
