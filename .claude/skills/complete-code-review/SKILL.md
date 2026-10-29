---
name: complete-code-review
description: Review the whole FerretSharp solution with parallel read-only agents by topic, write docs/review-X.Y.Z.md, present the findings by severity with proposed fixes, and implement the approved ones as an R package.
disable-model-invocation: true
argument-hint: "[focus]"
---
Talk to the user in German. Format and wording follow the earlier reviews: read `docs/review-3.44.0.md` first.

## 1. Prepare

- `git branch --show-current` must be an own branch, not `main` and not detached; otherwise refuse and stop. `git fetch origin main`; the branch must be at `origin/main` with a clean working tree – if not, say what differs and ask.
- Snapshot: note the HEAD SHA and the released version (latest tag `vX.Y.Z`); the review is called `X.Y.Z`. All agents read this SHA. If this session is not in its own worktree (other sessions may switch branches in the shared copy), create a detached worktree of the SHA in the scratchpad and point the agents there; remove it afterwards with `git worktree remove`.
- Context for the agents: the last `docs/review-*.md` (findings, decisions, "Stand"), `docs/backlog.md`, `docs/roadmap.md`, and the work packages since that review (`docs/work-packages/README.md`), plus `.claude/skills/branch-code-review/checklist.md`. Each agent also gets its brief from `agents.md` (next to this file): what it checks at least, and what it leaves to which other agent.

## 2. Eight agents by topic

Spawn eight `general-purpose` agents in one message, by topic (not by folder), all read-only (no edits, commits, checkout, stash). Only TE may run `dotnet build` and the unit tests; the others do not build (shared `obj`).

| ID | Topic |
|---|---|
| DB | DB access: read/write/DDL paths and their guards, SQL building, binds, transactions, sessions, Oracle types |
| ST | State and concurrency: workspaces, tabs, dispose, cancellation, silent VPN, background work |
| UI | UI/JS/CSS: components, JS interop and modules, grid, Monaco, shortcuts, tokens, en+de texts, render cost |
| PH | Host, processes, build: App, ModelHost, AppTrace, Velopack, CI workflows, project files, packages |
| SE | Security: the checklist's pass for every source listed there, each traced to its sinks; redaction, paths, processes |
| RE | Reuse and dead code: duplicates, copies that drift, unused code, readability, folder structure |
| TE | Tests: gaps, weak assertions, flaky or slow tests, structure, integration skips |
| DO | Docs: README, `CLAUDE.md` (still true? tokens per session), `.claude/rules/` (size, `paths` globs), architecture, ADRs, protocols, roadmap, backlog |

Every prompt contains: the snapshot path and SHA; the agent's block from `agents.md` verbatim; the review points toter Code, Duplikate, Lesbarkeit, Architektur, Sicherheit, Projekt- und Ordnerstruktur, Tests, Doku as far as the topic covers them; the focus from `$ARGUMENTS`, if any; "Findings from the last review and the backlog only if their implementation is missing or faulty; check that the last review's fixes took effect"; the answer format: German, IDs with the topic prefix (DB1, DB2 …), per finding severity (S/M/K), location `path:line`, finding with a concrete failure scenario, proposal, certainty (sicher / Verdacht), for structure proposals the effort (S/M/L); then "Ohne Befund geprüft".

Wait for all eight.

## 3. Consolidate and write the document

- Merge duplicates (name all IDs). Verify every S and M finding of part A by hand: **nachgeprüft**, otherwise the agent's assessment. Drop what contradicts a user decision.
- Write `docs/review-X.Y.Z.md` like the earlier ones: title with date; "Auftrag des Nutzers" (quote the request), the agents and the snapshot SHA; severity legend; "Ohne Befund geprüft (Auszug)"; **Teil A – Fehler und Sicherheit** (Schwer / Mittel / Klein, columns `# | IDs | Ort | Befund | Sicherheit`, each finding with a proposal); **Teil B – Struktur und Wiederverwendung** (`# | IDs | Vorschlag | Aufwand`); **Teil C – Tests** (gaps and weak tests, then "Überflüssig" with every test to merge or remove and the test that still catches its failure); **Teil D – Doku und CLAUDE.md**; **Fragen an den Nutzer**, always including scope and order (one package, or fixes as patch and structure as minor).
- Commit it alone (`docs: add review X.Y.Z`).

## 4. Present

As plain text in the chat (the wrap-up box is not visible on the phone): counts per part and severity, part A in full sorted S → M → K with the proposed fixes, parts B–D compact, then the questions. **Stop and wait for the decisions.**

## 5. Implement

- Record the answers as "Entscheidungen des Nutzers (YYYY-MM-DD)" in the review document.
- Implement as the next R package (`docs/work-packages/README.md`), branch `wp/r<n>-review-X.Y`, in this session without further agents. Order as in R7: pure moves first, then part A with tests, B, C, D, E2E last.
- A **Verdacht** is reproduced or measured before any change; what does not reproduce goes into the protocol with the reason.
- Trimming tests: before removing or merging, make sure the remaining test fails when the code under test is broken the same way (break it briefly, run, restore). Afterwards write the learnings into `.claude/rules/tests.md` › "Redundante Tests": what made the tests redundant, as a rule for new tests, with one example each – so later sessions writing tests apply it. Note the test count before and after in the protocol.
- Done as in `CLAUDE.md` §6: build `-warnaserror`, tests, en+de, ADRs, protocol `docs/work-packages/r<n>-review-X.Y.md` with index line, `CHANGELOG.md`, and the "Stand" line in the review document. Kept-but-not-done items go to `docs/backlog.md`.
- Second pass: before pushing, run `/branch-code-review` on the package branch, so the fixes get their own independent review.
- Finish with a short German summary (done, open, to test). Ask before pushing; the release runs separately via `/release`.
