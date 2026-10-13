# ADR 0025: Verbindungen importieren und exportieren – Quelle am Inhalt erkennen, DBeaver-User entschlüsseln, Passwörter nie

- Status: akzeptiert
- Datum: 2026-10-13
- Paket: WP-35

## Kontext

Ein Kollege des Nutzers fragte, ob sich Verbindungen aus einer anderen FerretSharp-Installation, aus DBeaver, HORA oder SQL Developer übernehmen lassen. Bisher blieb nur, `connections.json` von Hand zu kopieren – das ersetzt die eigenen Verbindungen, und Passwörter fehlen. Entscheidungen des Nutzers (Planung 2026-10-13, `docs/roadmap.md`): Passwörter nie mitnehmen, Duplikate standardmäßig überspringen, Prod immer schreibgeschützt, eine einmalige Kopie (keine geteilte Datei), Umfang FerretSharp, DBeaver und SQL Developer (HORA erst mit einer Beispieldatei).

Die Beispieldateien des Nutzers zeigten zwei Hürden: DBeaver speichert den **User** nicht in `data-sources.json`, sondern nur in der verschlüsselten `credentials-config.json`; SQL Developer kennt **keine Umgebung** (Dev/Test/Prod).

## Entscheidung

**Quelle am Inhalt erkennen, nicht an der Endung** (`ConnectionImportReader`): Zip → DBeaver-Projektexport (`.dbp`); JSON mit `format: ferretsharp-connections` oder `version` und Verbindungen mit `address` → FerretSharp; `connections` als Array mit `info` → SQL Developer; `connections` als Objekt → DBeaver. Fremdes JSON wird nachsichtig gelesen (`ImportJson`: fehlende oder anders getypte Werte sind null, keine Ausnahme). Obergrenzen: 16 MB je Datei bzw. Zip-Eintrag, 10.000 Zip-Einträge.

**DBeaver-User entschlüsseln, nur den User** (Entscheidung des Nutzers): `credentials-config.json` ist mit AES-128-CBC unter einem in DBeaver fest eingebauten, in dessen Dokumentation öffentlichen Schlüssel verschlüsselt, die ersten 16 Byte sind der IV. Ohne Entschlüsseln müsste der User jeder Verbindung von Hand eingetragen werden – bei vielen Verbindungen wäre der Import kaum eine Hilfe. `DbeaverCredentials` entschlüsselt die Datei als Ganzes (sie ist ein Block), geht das JSON mit einem `Utf8JsonReader` durch und macht nur `user` zu einem String; jeder andere Wert, das Passwort eingeschlossen, wird übersprungen, ohne gelesen zu werden, und der entschlüsselte Puffer danach überschrieben. Lässt sich die Datei nicht entschlüsseln, bleibt der User leer und wird in der Vorschau eingetragen. Die Gegenrichtung (`EncryptForTests`) gibt es nur für Tests, damit kein Test den Schlüssel kopiert und keine echte Credential-Datei ins Repository kommt.

**SQL Developer:** Export und die eigene `connections.json` der Installation haben dasselbe Format; ein gespeichertes `password` (rechnerbezogen verschlüsselt) wird nie gelesen. `NoPasswordConnection` taugt nicht als Merkmal (auch mit Passwort `"TRUE"`).

**Art raten, solange unsicher schreibgeschützt:** Ohne Angabe der Quelle (SQL Developer, eigene DBeaver-Typen) rät `ConnectionKindGuess` aus Wörtern des Namens (`prod`/`prd`/`live` vor `test`/`tst`/`qs`/`qa` vor `dev`/`develop`/`entw`; kurze Wörter nur ganz, lange auch als Anfang). Eine geratene Verbindung bleibt schreibgeschützt, bis der Nutzer in der Vorschau eine Art wählt – sonst wäre eine Prod-Verbindung als „Sonstige“ beschreibbar. Prod ist immer schreibgeschützt, ebenso was die Quelle so markiert.

**Vorschau als Zustand im Core** (`ConnectionImportPlan`, `ImportRow`): Auswahl, Art, User, Duplikat (gleiche Adresse – Host, Service, SID, Alias ohne Groß-/Kleinschreibung, Port genau – und gleicher User), Aktion bei Duplikaten (überschreiben behält Id, Passwort, Workspaces, lokale Pfade und – ohne Gruppe aus der Quelle – die Gruppe; oder als Kopie), Gruppe für Verbindungen ohne Gruppe. Gespeichert wird in einem Schritt (`ConnectionManager.SaveManyAsync`: einmal schreiben, ein `Changed`, nichts bei einer ungültigen Verbindung).

**Installationen finden ohne zu lesen** (`ConnectionInstallations`): DBeaver unter `%APPDATA%\DBeaverData\workspace6\<Projekt>\.dbeaver\data-sources*.json` (alle Projekte; Projektname als Gruppe außer `General`, ein DBeaver-Ordner geht vor), SQL Developer im neuesten `system<Version>` (numerisch verglichen) unter `o.jdeveloper.db.connection*`. Gelesen wird erst nach dem Klick. E2E-Tests richten die Suche mit `--import-appdata=<Ordner>` auf erfundene Dateien.

**Export** (`FerretSharpConnections`): das Dokument von `connections.json` plus `format`-Marke – so lässt sich eine fremde `connections.json` genauso importieren wie ein Export, und es gibt kein zweites Format zu pflegen. Ohne Passwörter, ohne `ClrProject`/`ScriptFolder` (außer auf Wunsch, für den Umzug auf einen eigenen neuen Rechner).

**Passwort beim Verbinden abfragen:** Importierte Verbindungen haben kein Passwort. Statt des Fehlers „Kein Passwort gespeichert“ fragt `WorkspaceLifecycle.ConnectAsync` danach (`PasswordDialog`): im Credential Manager speichern (Standard) oder nur bis zum Ende von FerretSharp (`ConnectionManager.SetPassword(…, remember: false)`, `GetPassword` nimmt das gespeicherte, sonst das der Sitzung – so nutzen auch die Workspace-Sessions es). Ein falsches Sitzungs-Passwort (ORA-01017) wird vergessen, damit der nächste Versuch wieder fragt.

## Konsequenzen

- TNS-Verbindungen aus DBeaver und SQL Developer zeigt die Vorschau als „nicht unterstützt“ – es gab kein Beispiel (Backlog). Ebenso OS-Anmeldung, Kerberos, Proxy, Rollen wie SYSDBA und JDBC-URLs außer den drei Thin-Formen.
- Ob DBeavers Schlüssel und Format zu echten Dateien passen, prüfen die Tests nur gegen selbst verschlüsselte Daten; die echte Datei des Nutzers wurde bei der Entwicklung bewusst nicht entschlüsselt. Prüfung durch den Nutzer in der App.
- Der Schema-Vergleich fragt wie bisher selbst nach Passwörtern (`NeedsPassword`), unabhängig von dieser Abfrage.
- HORA (KeepTool): keine Export-Funktion gefunden, wartet auf eine Beispieldatei (Backlog).
