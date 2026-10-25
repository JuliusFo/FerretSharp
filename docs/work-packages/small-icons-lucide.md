# Klein: Icons aus Lucide → Release offen
Anlass (2026-10-25): WP-61 brachte neun Icons, die Schnellleiste des Zellmenüs zeigt nur Icons mit der Bedeutung im Tooltip. Einige Motive waren schwer zu unterscheiden (Link gegen LinkView), „Vergleichen“ (Zeilen) nutzte das Icon des Schema-Vergleichs. Alle Icons waren seit den ersten Commits handgezeichnet; „keine Icon-Bibliothek“ war aus dem Bestand übernommen, keine Entscheidung. Branch `feat/icons`, ADR 0035.

Bestandsaufnahme vorher: 32 Icons in `Icons.cs` (ViewBox 16, Strich 1,4–1,8), `Key` ungenutzt. Verwendet in der Kopfleiste (Home, Suche, App-Statements, Schema-Vergleich, Einstellungen, Workspace entsperrt), im Explorer und Objektkopf (Plus, Neu laden, Arten von Tabellen und PL/SQL über `Icons.For`), in den Tabs (Art, Datensätze, LINQ, SQL, Anheften), in Tab-Werkzeugleisten (Neu laden, SQL anzeigen, Spalte springen), in Zell- und Spaltenmenü samt Export (WP-61) und in `SearchBox`/`SqlCode`. Die Statusleiste hat keine. Daneben dienen Textzeichen als Icons (✕ 35-mal, ▾, ⋯, Pfeile). Geprüft: eigene behalten, Lucide, Tabler, Fluent (NuGet-Paket und SVGs) und Webfont – Abwägung im ADR.

Entscheidungen des Nutzers:
- Option „Pfade aus einem frei lizenzierten Set übernehmen“, und zwar **Lucide**, **alle Icons** umstellen (nicht nur die aus WP-61).
- „Zeilen vergleichen“ bekommt ein eigenes Icon.
- Offen: ob die Textzeichen (✕ ▾ ⋯ Pfeile) auch auf Lucide umziehen – im Backlog.

Gebaut:
- `Icons.cs`: alle Icons aus `lucide-static` 1.55.0 (die neueste Version, die mindestens zwei Wochen alt war), Elemente unverändert, Lucide-Name in der Summary. `Svg()` setzt ViewBox 24 und Strich 2 für alle; in 14 px ist die Linie 1,17 px (vorher 1,23 px), die Größen im CSS bleiben.
- Neue und umbenannte Icons: `CompareRows` (`git-compare`) für „Markierte vergleichen“ (`FormQuickAction`), `SchemaCompare` (`file-diff`, vorher `Compare`) für die Kopfleiste. Motive mit neuer Bedeutung: Link auf Ansicht `bookmark` (vorher Kette mit Trichter), Formular `notebook-text`, Datensätze `workflow`, Einstellungen bleiben Schieberegler (`sliders-horizontal`). `SelectColumn` ist `columns-3` mit gefüllter Mittelspalte (die einzige eigene Zutat). `Key` entfällt.
- Lizenz: `wwwroot/lib/lucide/LICENSE.txt` (ISC und MIT-Text der Feather-Anteile) und `README.md` mit Version und Quelle, wie bei AG Grid und Monaco; README des Repos nennt Lucide unter „License“. `CLAUDE.md` hat eine Zeile „Icons“ im Stack, `.claude/rules/ui.md` die Regel für neue Icons.

E2E (eigene Instanz mit `--data-dir`, eigener Container `ferret-e2e-icons` auf 1531, danach entfernt; Screenshots per CDP `Page.captureScreenshot`, hell und dunkel per `Emulation.setEmulatedMedia`): Kopfleiste (Home, Suche, App-Statements, Schema-Vergleich, Einstellungen), Explorer mit Tabellen, Materialized View und den PL/SQL-Arten samt ungültigen (rot), Tabs (Tabelle angeheftet, Materialized View, SQL), Zellmenü mit einer Zeile (Formular), zwei Zeilen (Vergleichen mit eigenem Icon) und auf einer CLOB-Zelle (LOB), Export-Matrix, Spaltenmenü mit Beschriftung. Log ohne `[ERR]`. Nicht angesehen: LINQ-Tab, Datensatz-Tab, „entsperrt“ (nur Prod), C#-Wert (braucht das Modell), `SqlCode`, `SearchBox` – dort ändert sich nur die Form.

Offen: Die Screenshots im CHANGELOG zu 3.49.0 (`docs/images/changelog/3.49.0-*.png`) und die README-Bilder zeigen noch die alten Icons.
