# Changelog

All notable changes to this project are documented here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), versions follow [SemVer](https://semver.org/).

## [Unreleased]

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
