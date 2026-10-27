# Klein: Filter auf Enum-Werte, die das Modell nicht kennt

Branch `feat/enum-filter-any-value` (von `main`), 2026-10-28.

## Anlass
Wunsch des Nutzers (mit Screenshot): In einer Enum-Spalte steht ein Wert ohne Member (`1`, im Grid rot markiert), danach filtern ging nicht. Die Werteauswahl im Filter (`ValuePicker`, WP-12) bot für `=`, `≠` und `in` nur die Member an; ein Wert ohne Member stand nur drin, wenn er schon im Filter war (vor dem Laden des Modells getippt, aus einem FK-Sprung).

Entscheidung des Nutzers: ein freier Wert in der Auswahl (Variante A), kein eigener Filter „alle unbekannten Werte“ (Variante B).

## Gebaut
- Core `ValueTable.TypedValue(text)`: ein getippter Wert so, wie die Spalte ihn hält – Zahlen normalisiert (`2,0` → `2`, `2.50` → `2.5`), Text ohne umgebende Leerzeichen; null für leer oder eine Zahlenspalte, die den Text nicht aufnimmt.
- UI State `ValuePickerItems.Of(values, selected, typed)`: Member, dann gewählte Werte ohne Member (wie bisher); getippter Text filtert wie ein Menü (`MenuFilter`: jedes Wort in Name, C#-Member oder Wert). Ein getippter Wert ohne Member kommt als eigener Eintrag „kein Member“ zuerst – nicht, wenn er schon in der Liste steht oder der Text den Namen eines Members trifft (`offen` meint `Offen`, gespeichert als `OFFEN`).
- `ValuePicker`: Suchfeld oben im Popover, bekommt beim Öffnen den Fokus; ↑ ↓ wählen, Enter übernimmt (bei „in“ bleibt die Liste offen, die Suche leert sich), Esc schließt und gibt den Fokus an den Knopf zurück. Liste scrollt unter dem stehenbleibenden Feld (`.valpick-list`). Die Leertaste schaltet bei „in“ nicht mehr um – sie tippt ins Suchfeld.
- Texte Englisch und Deutsch (`ValuePicker_Search`, `ValuePicker_SearchTitle`).
- Tests: Core `ValueTableTests` (Zahl, Text), UI `ValuePickerItemsTests` (Reihenfolge, Suche, getippter Wert, keine Dopplung, Member-Name).

## E2E
2026-10-28: eigener Container `ferret-e2e-enum` (Port 1547, Beispielschema, danach entfernt), in KUNDEN eine Zeile mit `KUNDENART = 7` (kein Member von `Kundenart`); Build in den Scratchpad, eigene Instanz mit `--data-dir`, eigenem WebView2-Ordner, Debug-Port, Verbindung mit verknüpftem Beispielmodell, Passwort ohne Speichern. Tasten per CDP (Stufe 2):
- `=`: Klick auf die Auswahl → Suchfeld hat den Fokus, Liste Privat/Gewerbe/Behörde. `7` → nur „7 · kein Member“, markiert; Enter → Feld zeigt `7`, Liste zu, Fokus zurück auf dem Knopf. Anwenden → 1 Zeile, Wert rot markiert.
- `in`: ↓ auf dem Knopf öffnet mit Fokus im Suchfeld; `2` + Enter wählt Gewerbe, Liste bleibt offen, Suche leer; `9` + Enter und `11` + Enter → „Gewerbe, 7, 9, 11“; Esc schließt, Fokus auf dem Knopf. Gefunden und behoben: Nach Enter auf einen getippten Wert stand die Markierung auf dem ersten Eintrag statt auf dem neuen Wert (die Liste wurde vor dem neuen Wert berechnet).
- Maus per CDP: Klick auf „Behörde“ schaltet sie zu, Fokus bleibt im Suchfeld; Klick ins Suchfeld lässt die Liste offen, Klick daneben schließt sie; Anwenden mit Gewerbe, Behörde, 7, 9, 11 → ≥ 500 Zeilen.
- Log ohne `[ERR]`/`[WRN]`.

Nicht mit echter Eingabe geprüft: Per `SendInput` kamen nur Bewegungen an (Markierung folgte der Maus), kein `mousedown` – wie in `docs/e2e-testing.md` beschrieben, nicht weitergeklickt.
