<p align="center">
  <img src="tools/icon/ferretsharp-256.png" width="96" height="96" alt="FerretSharp logo">
</p>

<h1 align="center">FerretSharp</h1>

<p align="center">
  An Oracle database explorer for .NET developers – with workspaces, foreign-key navigation,<br>
  safe sandbox editing and a bridge to your EF Core model.
</p>

<p align="center">
  <a href="https://github.com/JuliusFo/FerretSharp/actions/workflows/ci.yml"><img src="https://github.com/JuliusFo/FerretSharp/actions/workflows/ci.yml/badge.svg" alt="CI"></a>
  <a href="https://github.com/JuliusFo/FerretSharp/releases/latest"><img src="https://img.shields.io/github/v/release/JuliusFo/FerretSharp" alt="Latest release"></a>
  <img src="https://img.shields.io/badge/.NET-10-512BD4" alt=".NET 10">
  <img src="https://img.shields.io/badge/platform-Windows-0078D4" alt="Windows">
  <a href="LICENSE"><img src="https://img.shields.io/github/license/JuliusFo/FerretSharp" alt="MIT license"></a>
</p>

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/images/grid-dark.png">
  <img src="docs/images/grid-light.png" alt="FerretSharp: a filtered table with C# property names and enum names in the grid">
</picture>

> [!NOTE]
> The user interface is in German. Code, commits and this README are in English; the design notes in `docs/` are German.

## Why

General-purpose database tools know your schema, but not your application. FerretSharp is built around the way a .NET
developer works with an Oracle database day to day:

- **Workspaces** per connection – "Bug 3711", "Export Q3" – each with its own tabs, filters and its **own database session**,
  so two workspaces can look at (and later edit) the same table independently.
- **Follow the data**: jump from a row to the rows it references or that reference it – also along relationships that
  exist only in your EF Core model, without a foreign-key constraint.
- **Edit without fear**: every change runs in an explicit transaction you commit or roll back; production connections are
  read-only by default, enforced by Oracle itself.
- **Speak C#**: entity and property names next to tables and columns, enum members instead of magic numbers, and a LINQ
  console that shows the SQL your code really sends.

## Features

### Browse

- Explorer with letter index and search; tables, views, materialized views and objects reachable through synonyms.
- Fast grid for large tables (blocks of 500 rows, server-side sorting, deterministic paging), `NULL` shown as such,
  exact numbers beyond `decimal`, LOB previews, pinned columns, column search (Ctrl+F).
- Composable filters in the style of TablePlus – column, operator, value – translated into bound SQL; see the generated
  statement and its execution plan at any time.
- Object details per table: columns, constraints, indexes (with a hint for unindexed foreign keys), dependencies, DDL.
- Copy rows as table, `INSERT` statements or C# object initializers; save as CSV or SQL script.

<table>
  <tr>
    <td width="50%">
      <picture>
        <source media="(prefers-color-scheme: dark)" srcset="docs/images/fk-navigation-dark.png">
        <img src="docs/images/fk-navigation-light.png" alt="Context menu with foreign-key navigation, including a relationship from the C# model">
      </picture>
    </td>
    <td width="50%">
      <b>Foreign-key navigation</b><br><br>
      Right-click a cell: jump to the referenced row, or to the rows that reference it – with counts, loaded lazily.
      Each jump opens a new tab with the filter already applied; <kbd>Alt</kbd>+<kbd>←</kbd> takes you back.
      Relationships that exist only as navigations in your DbContext are marked "aus C#-Modell".
    </td>
  </tr>
</table>

### Edit safely

- **Sandbox editing**: changes are *pending* (local), then *written* (DML executed in the workspace's transaction,
  rows locked), then *committed* – each state visible in the grid. Undo the last write, roll back everything.
- Lock conflicts are detected after a short wait (`SELECT … FOR UPDATE WAIT n`) and shown with the blocking session.
- Optimistic concurrency check on the changed columns; insert and delete rows; edit CLOBs and BLOBs in a dialog.
- **Read-only connections** (default for production) run in `SET TRANSACTION READ ONLY` – Oracle rejects DML. Unlock a
  single workspace on purpose by typing the connection's name.

### SQL

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/images/sql-editor-dark.png">
  <img src="docs/images/sql-editor-light.png" alt="SQL editor with bind variables and a result grid">
</picture>

- SQL editor (Monaco) per workspace: <kbd>Ctrl</kbd>+<kbd>Enter</kbd> runs the statement at the cursor,
  <kbd>Alt</kbd>+<kbd>X</kbd> the whole script; queries everywhere, `INSERT`/`UPDATE`/`DELETE`/`MERGE` only in writable
  workspaces, inside their transaction.
- Bind variables (`:kundeId`) become typed input fields – values are bound, never pasted into the SQL.
- Completion for tables, views, synonyms and columns (with their C# names), history per connection, export of results.
- Execution plans: estimated (`EXPLAIN PLAN`) or actual, with row counts from the real execution and misestimates flagged.

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/images/explain-plan-dark.png">
  <img src="docs/images/explain-plan-light.png" alt="Execution plan dialog">
</picture>

### .NET and EF Core

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/images/linq-console-dark.png">
  <img src="docs/images/linq-console-light.png" alt="LINQ console with completion, the SQL EF Core generates and the result">
</picture>

Link a connection to your EF Core project (EF Core 8 or later) and FerretSharp loads the compiled model in a separate
process – using your project's runtime, provider and value converters:

- **Names**: entity and property names next to tables and columns (or in front of them), searchable everywhere.
- **Values**: enum members and converted values (`Gewerbe (2)`, `true` for `'J'`), also as dropdowns in filters and
  when editing – including `[Display]` names from your resources.
- **Relationships**: navigations without a foreign-key constraint become navigable relationships.
- **LINQ console**: paste a query from your code – unknown variables become suggested declarations, your context and
  cancellation token are recognized. FerretSharp shows the SQL EF Core would send and runs it in the workspace's session,
  so your uncommitted changes are visible. Completion knows your DbSets, properties, enums and the LINQ/EF methods.
- **Code generation**: the grid's filters as a LINQ `Where`, rows as C# objects or `HasData` seed data.

## Getting started

**Requirements**

- Windows 10 or 11, x64 (the WebView2 runtime is part of Windows).
- Oracle Database 19c or later (12.2 should work). Plain TCP connections (host/port with service name or SID, or a TNS
  alias); no Oracle client installation needed.
- Optional, for the .NET features: an EF Core 8+ project that builds, and the .NET SDK.

**Install**

1. Download `FerretSharp-x.y.z-win-x64.zip` from the [latest release](https://github.com/JuliusFo/FerretSharp/releases/latest).
2. Unzip it anywhere and start `FerretSharp.exe` – no installer, no admin rights.

Settings and workspaces live in `%APPDATA%\FerretSharp`; passwords are stored in the Windows Credential Manager, never in
files.

> [!IMPORTANT]
> FerretSharp guards against accidental writes in many places, but the only real guarantee is on the database side:
> **for production, use a database user with SELECT grants only.**

## Keyboard shortcuts

| Keys | Action |
|---|---|
| <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>O</kbd> | Switch connection |
| <kbd>Ctrl</kbd>+<kbd>Enter</kbd> | Apply filters · run the statement at the cursor (SQL) · run the code (LINQ) |
| <kbd>F5</kbd> | Refresh |
| <kbd>Ctrl</kbd>+<kbd>F</kbd> | Find a column · search in the editor |
| <kbd>Alt</kbd>+<kbd>←</kbd> / <kbd>Alt</kbd>+<kbd>→</kbd> | Back / forward along foreign-key jumps |
| <kbd>Ctrl</kbd>+<kbd>S</kbd> | Write pending changes (no commit) |
| <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>Enter</kbd> | Commit |
| <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>Q</kbd> / <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>L</kbd> | New SQL editor / new LINQ console |
| <kbd>Alt</kbd>+<kbd>X</kbd> | Run the whole SQL script |

## Building from source

You need the .NET 10 SDK (pinned in `global.json`) and, for the integration tests, Docker.

```shell
dotnet build
dotnet test
dotnet run --project src/FerretSharp.App
```

- Integration tests start Oracle Free in Docker ([Testcontainers](https://dotnet.testcontainers.org/)) and are skipped
  when Docker is not available.
- `tools/sample-db/New-SampleDb.ps1` creates a local sample database with the schema used in the screenshots;
  `samples/` contains a matching EF Core project.
- A release build is a self-contained folder:
  `dotnet publish src/FerretSharp.App -c Release -r win-x64 --self-contained -o <folder>`.

**Tech stack:** .NET 10, WPF host with Blazor Hybrid (WebView2), [AG Grid Community](https://www.ag-grid.com/),
[Monaco Editor](https://microsoft.github.io/monaco-editor/), Oracle.ManagedDataAccess.Core, Roslyn for the LINQ console.

**Design notes:** [CHANGELOG.md](CHANGELOG.md) lists every release; architecture decisions are in
[docs/decisions](docs/decisions), ideas in [docs/backlog.md](docs/backlog.md) (both in German).

## License

[MIT](LICENSE). AG Grid Community and Monaco Editor are included under their MIT licenses
(see `src/FerretSharp.UI/wwwroot/lib`).
