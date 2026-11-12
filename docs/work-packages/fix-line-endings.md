# Fix: Coverage-Badge auf main an Zeilenenden gescheitert

Branch `fix/line-endings` (von `main`), 2026-11-12, PR #178, noch ohne Release.

## Anlass (Meldung des Nutzers, 2026-11-12)
- „Das CI auf main ist gefailed“: Lauf 43249065190 nach dem Merge von #177. Build und Tests grün, der Job „Coverage badge“ brach in `git switch --force-create badges FETCH_HEAD` ab („Your local changes … would be overwritten by checkout: src/FerretSharp.Core/Connections/ConnectionSearch.cs“).
- Ursache: `fdeef9df` hatte in `ConnectionSearch.cs` die 18 eingefügten Zeilen (67–84) mit CRLF committet, der Rest LF. Wegen `*.cs text` in `.gitattributes` ist so eine Datei nach dem Checkout geändert. Wie die Zeilen an der Normalisierung von `git add` vorbeikamen, ist nicht geklärt.

## Entscheidungen des Nutzers (2026-11-12)
- „Ja, PR öffnen“ für die normalisierte Datei.
- Auf die Frage nach einer Regel für Claude: keine Regel, stattdessen „Ja, CI-Check rein“.
- Code-Review: „Alle beheben“. Offene Fragen mit der einfachsten Variante entschieden: nur `*.md text` (nur dort gab es ein Problem), Check in `Test-Linux.ps1` eingebaut statt die Lücke nur zu beschreiben.

## Gebaut
- `ConnectionSearch.cs` auf LF (`git add --renormalize`, Inhalt unverändert).
- `.github/workflows/ci.yml`: eigener Job `line-endings` (`git add --renormalize .`, bricht ab, wenn danach etwas gestaged ist, Liste per `git diff --cached --stat`); der Badge-Schritt wechselt mit `--discard-changes`.
- `.gitattributes`: `*.md text`; `wp-84-script-targets.md` ohne die 10 × `CR CR LF`, wegen derer Git sie als binär führte.
- `tools/linux/Test-Linux.ps1`: bricht vorher lesend ab, wenn eine Textdatei im Index CRLF hat (`git ls-files --eol`).
- Doku: `docs/release.md` (CI › Zeilenenden, Lokal wie der Linux-Job), CHANGELOG (Internal).
- Geprüft per Nachstellung in eigenen Test-Repos (gemischte Datei, Namen mit Leerzeichen und Umlaut, `--orphan`/`--force-create` mit `--discard-changes`); der Badge-Job selbst erst mit dem Lauf auf `main` nach dem Merge.

## Nachtrag: Code-Review (2026-11-12)
Entscheidung des Nutzers: „Alle beheben“.
1. M – Check als erster Schritt des Linux-Jobs übersprang bei einem Treffer Build und Tests: behoben, eigener Job `line-endings`.
2. K – Dateiliste im Fehlerfall ungequotet und nach dem Renormalisieren: behoben, `git -c core.quotePath=false diff --cached --stat`.
3. K – einzelnes CR macht eine Datei unter `text=auto` binär, Check sieht sie nicht (wp-84): behoben, `*.md text`, wp-84 bereinigt.
4. K – Kommentar und Doku nannten `text=auto` statt `*.cs text` als Ursache: behoben.
5. K – CHANGELOG-Eintrag fehlte: behoben (Internal).
6. K – lokal keine Prüfung: behoben, `Test-Linux.ps1`.
- Zweiter Durchgang, Entscheidung des Nutzers „Alle beheben“:
7. K – der lokale Check übersah ein einzelnes CR unter `*.md text` (`i/-text attr/text`), die CI meldet es: behoben, Filter um `i/-text` mit ausdrücklichem `text` erweitert.
8. K – `git add --renormalize` behebt `CR CR LF` nicht (nur das CR vor dem LF fällt weg): behoben, Hinweis in CI, `Test-Linux.ps1` und `release.md`.
9. K – „noch ohne Release“ im Kopf, Job im Kopfkommentar von `ci.yml`, `tools/linux/*.sh` in `release.md`: behoben.
- Dritter Durchgang, Entscheidung des Nutzers „Beides beheben“:
10. K – der erweiterte lokale Filter meldete auch Dateien mit einzelnem CR oder NUL ohne CRLF, die die CI durchlässt: behoben, für `i/-text` unter `text` wird der Blob wie bei `git add` neu gehasht (`git hash-object --stdin --path`, schreibt nichts); nachgestellt, gleiche Treffer wie die CI auf einem frischen Clone.
- Vorher schon da: Auf `main` waren keine Status-Checks Pflicht. Nutzer: „Können wir den Pflicht-Status-Check direkt machen?“, Bypass „Admin darf umgehen“. Umgesetzt im Ruleset „Protect main“ (Linux, Windows, Line endings; Repo-Admin mit Bypass `always`), Doku in `release.md` und `build-release.md`.
- Vorher schon da, mit behoben – `pages/** text eol=lf` gewann gegen `*.png binary`, ein Bild unter `pages/` wäre beim `git add` beschädigt worden: zuerst `pages/**/*.png` und `*.ico` binär, dann durch Punkt 13 ersetzt.
- Vierter Durchgang, Entscheidung des Nutzers „Beheben und pushen“ (ohne weiteren Reviewer-Durchgang):
11. K – der Hash-Vergleich braucht pwsh 7.4+ (Bytes unverändert durch die Pipe), Windows PowerShell 5.1 hätte falsch gemeldet: behoben, `#Requires -Version 7.4`.
12. K – Pfade mit `"`, `\`, Tab oder Zeilenumbruch kamen gequotet in `--path=`: behoben, `ls-files -z`.
13. K – nur `.png`/`.ico` waren unter `pages/` binär, andere Bilder oder Fonts wären beschädigt worden: behoben, `pages/` nur noch mit Textendungen (`html`, `js`, `css`, `svg`, `json`, `txt`) als `text eol=lf`; Zeile in `.claude/rules/build-release.md`.
- Vorher schon da: Auf `main` sind keine Status-Checks Pflicht; ein PR lässt sich rot mergen.
