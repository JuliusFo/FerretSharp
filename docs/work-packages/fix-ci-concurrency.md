# Fix: CI-Lauf auf main nach dem Merge abgebrochen

Branch `fix/ci-concurrency` (von `main`), 2026-11-10, noch ohne Release.

## Anlass (Meldung des Nutzers, 2026-11-10)
- Nach dem Merge von WP-82 (PR #169) wurde der CI-Lauf auf `main` sofort abgebrochen: „Canceling since a higher priority waiting request for ci-refs/heads/main exists“.
- Ablauf: Der letzte Commit des Branches wurde um 15:24 gepusht, GitHub legte den PR-Lauf dafür aber erst um 15:27:55 an – nach dem Merge (15:27:50) und nach dem Start des main-Laufs (15:27:53), ohne Verbindung zum PR (`pull_requests: []`). Dessen `github.ref` war nicht mehr `refs/pull/169/merge`, der Lauf lag in der Gruppe `ci-refs/heads/main` und brach als neuerer den main-Lauf ab.
- Belegt: Der neu gestartete main-Lauf brach seinerseits diesen PR-Lauf mit derselben Meldung („… for ci-refs/heads/main“) ab. Vermutlich derselbe Grund: der abgebrochene main-Lauf von `16d8669d` (Release 3.68.1) am Vormittag.

## Entscheidung des Nutzers (2026-11-10)
- „Beides“: den abgebrochenen Lauf neu starten und die Gruppe fixen.

## Gebaut
- `.github/workflows/ci.yml`: `concurrency.group` = `ci-pr-<Nummer>` für PR-Läufe, sonst wie bisher `ci-<ref>`. Ein neuer Push auf denselben PR bricht den älteren Lauf weiter ab, ein verspäteter PR-Lauf trifft `main` nicht mehr.
- Doku: `docs/release.md` (CI › Concurrency), Regel in `.claude/rules/build-release.md`, CHANGELOG (Internal).
- Nicht lokal testbar; geprüft wird es mit der CI dieses PRs (Gruppe `ci-pr-…`) und dem Lauf auf `main` nach dem Merge.
