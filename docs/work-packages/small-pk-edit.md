# Klein: Primärschlüssel bestehender Zeilen ändern → Release offen

Branch `feat/pk-edit` (von `main`), 2026-11-07.

## Anlass
Ein Kollege des Nutzers stellte fest, dass sich PK-Werte bestehender Zeilen nicht ändern lassen (ADR 0007: Entscheidung von WP-09, „PK ist Zeilenidentität und Ziel referenzierender FKs“). Der Umweg war ein `UPDATE` im SQL-Editor. Der Nutzer will PKs ab und zu ändern können.

## Entscheidungen des Nutzers
- Verweisende Zeilen: **prüfen und warnen** – vor dem Schreiben zählen (deklarierte und virtuelle Beziehungen), bei Treffern nachfragen; nichts mitziehen (Alternativen waren „einfach freigeben“ und „Kinder mitziehen“, Letzteres in den Backlog).
- Befehle für viele Zellen („Wert setzen…“, Nach unten füllen, Entf, Einfügen in Markierungen): **überall** erlaubt, doppelte Schlüssel meldet Oracle.

## Gebaut
- Core: `OracleTypeMapper.NotEditableReason` ohne PK-Sperre und ohne Parameter `newRow` (er hatte nur diese Bedeutung); Text `NotEditablePrimaryKey` entfernt. Grid: `GridColumn.EditableNew` entfällt, neue und geladene Zeilen nutzen `editable`.
- `DmlBuilder.Update` hängt bei geändertem Row-Key (`ChangesKey`) `RETURNING ROWID INTO :p_rowid` an; `OracleDataEditor` liest die Zeile darüber neu (`ReadKeyAsync`, auch für INSERT) und gibt ihren Schlüssel in `FlushResult.NewKeys`.
- `ChangeTracker.MarkFlushed`/`UndoFlush` hängen eine Änderung mit neuem Schlüssel um (`Rekey`).
- `Core/Query/KeyChanges`: `Of` (Updates, die den PK ändern), `References` (je eingehender Beziehung in eine geänderte Spalte ein `FkJump` mit den alten Werten, wie die Datenbank sie in der Transaktion hält – `ExpectedOf`).
- UI State `WorkspaceEditing.Keys.cs`: Vor jedem Schreiben eines Tabs (`FlushTabAsync`, also auch Commit und Redo) werden die Verweise gezählt (`FkNavigation.CountAsync`, Zeitlimit 5 s je Beziehung); Treffer, Zeitüberschreitung oder Fehler → `KeyQuestion`, Dialog `KeyChangeDialog` („Trotzdem schreiben“ setzt Schreiben/Commit/Redo fort, Abbrechen lässt alles ausstehend). Die Bestätigung gilt wie „Überschreiben“ nur für diesen Versuch (`ClearDecisions`).
- Texte EN/DE, ADR 0007 Nachtrag, README, CHANGELOG, Backlog.

## Fallen
- Typen des neuen Schlüssels: Der getippte Wert ist nicht unbedingt, was ein Neuladen liest (CHAR wird aufgefüllt, Trigger) – deshalb liest der Schreibweg die Zeile über die ROWID neu, statt den Schlüssel aus den getippten Werten zu bauen. Sonst fände der Tracker die neu geladene Zeile nicht.

## E2E
Eigene Testinstanz (`--lang=de`, eigener Datenordner) gegen einen eigenen Container (Beispielschema plus zwei Tabellen mit neutralen Namen nach dem Muster der Tabelle des Kollegen: eine Verknüpfungstabelle, deren zwei Spalten zusammen der PK sind und je per FK mit `ON DELETE CASCADE` auf dieselbe Elterntabelle zeigen). Doppelklicks, ↶/↷, Rollback und Dialogknöpfe mit echter Windows-Eingabe (`Invoke-RealClick.ps1`), Tippen und Ctrl+S/Ctrl+Z per CDP.
- Verknüpfungstabelle: Zelleditor auf der PK-Spalte öffnet; (1, 2) → (1, 5) ohne Rückfrage geschrieben (keine eingehenden Beziehungen), die Markierung „geschrieben“ hängt nach dem Neuladen an (1, 5); dieselbe Zeile weiter über den neuen Schlüssel geändert zu (6, 5). Zweimal ↶: erst (1, 5) mit der 6 ausstehend, dann (1, 2) mit 6 und 5 ausstehend; zweimal ↷ schreibt beides wieder.
- Elterntabelle: 4 → 40 (eine Zeile der Verknüpfungstabelle zeigt darauf): Dialog listet nur die Beziehung über die betroffene Spalte mit 1 Zeile („Constraint – Oracle lehnt ab“), die zweite Beziehung (0 Zeilen) fehlt; Abbrechen hat den Fokus. „Trotzdem schreiben“ → Schreibproblem-Dialog mit ORA-02292 und Constraint-Namen, Änderung bleibt ausstehend. Erneutes Ctrl+S fragt wieder; Abbrechen lässt sie ausstehend. 6 → 60 (nichts verweist darauf) ohne Rückfrage geschrieben.
- Log ohne Warnungen und Fehler.
- Nicht geprüft: Verweise nur über eine Beziehung des C#-Modells (Weg wie bei deklarierten, im UI-Test abgedeckt), Zeitüberschreitung beim Zählen.

## Nachtrag: Code-Review (2026-11-07)
Unabhängiger Reviewer, jeder Befund nachgeprüft. Entscheidung des Nutzers: „Jo, passt.“ (alle Punkte 1–10, Zählbudget 10 s, bei nicht geladenem C#-Modell trotzdem fragen mit Hinweis); nach dem zweiten Durchgang „Jo, fix das“ (11–13).
1. M – Zählfehler außer Oracle-Fehlern (Filterprüfung: Spalte einer Beziehung fehlt, Typ passt nicht) ließen jedes Schreiben scheitern: behoben, alles außer Abbruch/Verbindungsverlust ist „nicht gezählt“; `TestApp` prüft Filter wie die Session (`A_relationship_the_table_no_longer_has_the_column_of_is_shown_as_not_counted`).
2. M – Nicht erzwungener PK: editierbar, aber ohne Prüfung der Verweise: behoben, `KeyChanges.Of` hängt nicht an der Erzwingung (`A_key_that_is_not_enforced_is_a_key_change_too`).
3. M – Formular verlor die Zeile nach dem Schreiben einer PK-Änderung: behoben, `ChangeTracker.KeyNow` + `FormCursor` (`A_row_whose_primary_key_was_written_is_followed_by_its_new_key`).
4. M – Zählen N × 5 s ohne Abbruch, nach 5 s „Session trennen“: behoben, ein Budget von 10 s, der Rest „nicht gezählt“ (`Counting_shares_one_budget_and_never_counts_as_stuck`).
5. K – Geschriebene Löschung fiel aus dem Tracker, wenn eine andere Zeile ihren Schlüssel übernahm (auch INSERT, vorher schon so): behoben, `_displaced` (`A_written_delete_whose_key_another_row_takes_stays_counted_and_comes_back_on_undo`).
6. K – Eingefügte Zeile wurde mit dem getippten statt dem gespeicherten Schlüssel gesucht: behoben (`A_row_inserted_in_the_transaction_is_looked_for_by_its_stored_key`).
7. K – „Trotzdem schreiben“ blieb nach einem abgelehnten Commit bestätigt und ließ einen anderen Wert durch: behoben, die Bestätigung gilt für die Werte (`A_confirmation_covers_only_the_values_confirmed`).
8. K – Ohne Schema bzw. ohne geladenes C#-Modell wurde still geschrieben: behoben, Frage mit Hinweis (`A_linked_model_that_is_not_loaded_is_named_and_asked_about`).
9. K – „Oracle lehnt ab“ stimmte nicht für deaktivierte und aufgeschobene Constraints: Text geändert.
10. K – ADR-Folgen, Backlog, `NewKeys`-Kommentar, resx-Kommentare: nachgezogen.
- Verworfen: Zählen sieht ausstehende Änderungen anderer Tabs nicht – es zählt bewusst, was in der Transaktion steht.
- Vorher schon da, in den Backlog: Redo-Eintrag nach einem Fehler, der kein Schreibproblem ist; Zeile, die sich nach dem Schreiben nicht zurücklesen lässt.

Zweiter Durchgang über die Fixes:
11. M – Nach einem Rollback machte das Formular aus dem alten Schlüssel wieder den neuen und verlor die Zeile; ein neu eingefügter Schlüssel löschte den Vermerk nicht: behoben, `KeyNow` nur, wenn der alte Schlüssel nicht mehr geladen ist (`After_a_rollback_the_row_keeps_its_old_key_in_the_form`, `A_key_a_row_moved_away_from_belongs_to_a_row_inserted_with_it`).
12. M – Die Struktur der verweisenden Tabelle wurde ohne Zeitlimit gelesen, und Zählen galt nie als hängend: behoben, Struktur im Zeitlimit, „hängt“ nach Budget + 5 s (`A_structure_that_never_arrives_ends_with_the_time_limit`, `Counting_longer_than_the_budget_and_5_s_counts_as_stuck`).
13. K – Der Timer für „Session trennen“ verpasste die verschobene Fälligkeit: behoben, er stellt sich neu (ohne Test, Komponente).

Nicht in der App geprüft: Formular nach PK-Änderung, Hinweis bei nicht geladenem C#-Modell, Zeitbudget und „Session trennen“ beim Zählen.
